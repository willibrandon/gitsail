using GitSail.Domain;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace GitSail.Git.Execution;

/// <summary>
/// Captures stable exact tracked, untracked, and ignored worktree identities through Git.
/// </summary>
internal sealed class RepositoryWorktreeFingerprintService
{
    private const int MaximumStableCaptureAttempts = 3;
    private const int MaximumPathListBytes = 512 * 1024 * 1024;
    private const int MaximumStatusBytes = 512 * 1024 * 1024;
    private const int MaximumErrorBytes = 4 * 1024 * 1024;
    private const int MaximumPathBytes = 16 * 1024 * 1024;
    private const int MaximumPathCount = 1_000_000;
    private const int MaximumBatchPaths = 128;
    private const int MaximumBatchArgumentUnits = 20 * 1024;
    private static readonly UTF8Encoding s_strictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private readonly GitInstallation _installation;
    private readonly IChildProcessRunner _runner;
    private readonly GitChildEnvironmentFactory _environmentFactory;
    private readonly RawDiffService _rawDiffService;

    /// <summary>
    /// Initializes complete worktree fingerprint capture over the sole process boundary.
    /// </summary>
    /// <param name="installation">The resolved Git installation.</param>
    /// <param name="runner">The sole child-process runner.</param>
    /// <param name="environmentFactory">The operation-specific child-environment factory.</param>
    internal RepositoryWorktreeFingerprintService(
        GitInstallation installation,
        IChildProcessRunner runner,
        GitChildEnvironmentFactory environmentFactory)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(environmentFactory);
        _installation = installation;
        _runner = runner;
        _environmentFactory = environmentFactory;
        _rawDiffService = new RawDiffService(installation, runner, environmentFactory);
    }

    /// <summary>
    /// Captures one stable complete worktree fingerprint or rejects continuing changes.
    /// </summary>
    /// <param name="workingDirectory">The canonical repository working directory.</param>
    /// <param name="cancellationToken">Signals fingerprint capture cancellation.</param>
    /// <returns>The exact stable SHA-256 worktree identity.</returns>
    internal async Task<RepositoryWorktreeFingerprint> CaptureAsync(
        CanonicalDirectory workingDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);
        for (var attempt = 0; attempt < MaximumStableCaptureAttempts; attempt++)
        {
            var first = await CaptureOnceAsync(workingDirectory, cancellationToken).ConfigureAwait(false);
            var second = await CaptureOnceAsync(workingDirectory, cancellationToken).ConfigureAwait(false);
            if (first is not null && second is not null && first.Matches(second))
            {
                return second;
            }
        }

        throw new RepositoryPreconditionException(
            "Tracked, untracked, or ignored files continued changing while GitSail captured the worktree; retry the refresh.");
    }

    private async Task<RepositoryWorktreeFingerprint?> CaptureOnceAsync(
        CanonicalDirectory workingDirectory,
        CancellationToken cancellationToken)
    {
        var statusBefore = await ReadStatusAsync(workingDirectory, cancellationToken).ConfigureAwait(false);
        using var worktreeDiff = await _rawDiffService.CaptureAsync(
            workingDirectory,
            RawDiffTarget.WorkTree,
            new OperationGeneration(0),
            contextLines: 0,
            cancellationToken).ConfigureAwait(false);
        var trackedDigest = await worktreeDiff.ComputeSha256Async(cancellationToken).ConfigureAwait(false);
        var untrackedBytes = await ReadOtherPathsAsync(
            workingDirectory,
            includeIgnored: false,
            cancellationToken).ConfigureAwait(false);
        var ignoredBytes = await ReadOtherPathsAsync(
            workingDirectory,
            includeIgnored: true,
            cancellationToken).ConfigureAwait(false);
        var untrackedPaths = ParsePaths(untrackedBytes.Span);
        var ignoredPaths = ParsePaths(ignoredBytes.Span);
        var untrackedObjectIds = await HashPathsAsync(
            workingDirectory,
            untrackedPaths,
            cancellationToken).ConfigureAwait(false);
        var ignoredObjectIds = await HashPathsAsync(
            workingDirectory,
            ignoredPaths,
            cancellationToken).ConfigureAwait(false);
        var statusAfter = await ReadStatusAsync(workingDirectory, cancellationToken).ConfigureAwait(false);
        if (!statusBefore.Span.SequenceEqual(statusAfter.Span))
        {
            return null;
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendSegment(hash, "status"u8, statusAfter.Span);
        AppendSegment(hash, "tracked"u8, trackedDigest);
        AppendSegment(hash, "untracked-list"u8, untrackedBytes.Span);
        AppendObjectIds(hash, "untracked-objects"u8, untrackedObjectIds);
        AppendSegment(hash, "ignored-list"u8, ignoredBytes.Span);
        AppendObjectIds(hash, "ignored-objects"u8, ignoredObjectIds);
        return new RepositoryWorktreeFingerprint(hash.GetHashAndReset());
    }

    private async Task<ReadOnlyMemory<byte>> ReadStatusAsync(
        CanonicalDirectory workingDirectory,
        CancellationToken cancellationToken)
    {
        var result = await RunReadAsync(
            workingDirectory,
            [
                ProcessArgument.Literal("status"),
                ProcessArgument.Literal("--porcelain=v2"),
                ProcessArgument.Literal("-z"),
                ProcessArgument.Literal("--untracked-files=all"),
                ProcessArgument.Literal("--ignored=matching"),
                ProcessArgument.Literal("--no-renames"),
            ],
            MaximumStatusBytes,
            "Git could not capture complete worktree status.",
            cancellationToken).ConfigureAwait(false);
        return result.StandardOutput;
    }

    private async Task<ReadOnlyMemory<byte>> ReadOtherPathsAsync(
        CanonicalDirectory workingDirectory,
        bool includeIgnored,
        CancellationToken cancellationToken)
    {
        var arguments = new List<ProcessArgument>
        {
            ProcessArgument.Literal("ls-files"),
            ProcessArgument.Literal("--others"),
            ProcessArgument.Literal("--exclude-standard"),
            ProcessArgument.Literal("--full-name"),
            ProcessArgument.Literal("-z"),
        };
        if (includeIgnored)
        {
            arguments.Add(ProcessArgument.Literal("--ignored"));
        }

        var result = await RunReadAsync(
            workingDirectory,
            arguments,
            MaximumPathListBytes,
            includeIgnored
                ? "Git could not enumerate ignored worktree paths."
                : "Git could not enumerate untracked worktree paths.",
            cancellationToken).ConfigureAwait(false);
        return result.StandardOutput;
    }

    private async Task<List<ObjectId>> HashPathsAsync(
        CanonicalDirectory workingDirectory,
        List<GitPath> paths,
        CancellationToken cancellationToken)
    {
        var objectIds = new List<ObjectId>(paths.Count);
        var offset = 0;
        while (offset < paths.Count)
        {
            var count = GetBatchCount(paths, offset);
            var arguments = new List<ProcessArgument>(count + 3)
            {
                ProcessArgument.Literal("hash-object"),
                ProcessArgument.Literal("--no-filters"),
                ProcessArgument.Literal("--"),
            };
            for (var index = 0; index < count; index++)
            {
                arguments.Add(ProcessArgument.Native(paths[offset + index]));
            }

            var result = await RunReadAsync(
                workingDirectory,
                arguments,
                checked(count * 66),
                "Git could not hash an untracked or ignored worktree path.",
                cancellationToken).ConfigureAwait(false);
            ParseObjectIds(result.StandardOutput.Span, count, objectIds);
            offset += count;
        }

        return objectIds;
    }

    private async Task<ProcessResult> RunReadAsync(
        CanonicalDirectory workingDirectory,
        List<ProcessArgument> arguments,
        int maximumOutputBytes,
        string fallbackError,
        CancellationToken cancellationToken)
    {
        var completeArguments = new List<ProcessArgument>(arguments.Count + 2)
        {
            ProcessArgument.Literal("--literal-pathspecs"),
            ProcessArgument.Literal("--no-pager"),
        };
        completeArguments.AddRange(arguments);
        var invocation = new ProcessInvocation(
            _installation.Executable,
            [.. completeArguments],
            workingDirectory,
            _environmentFactory.CreateRepositoryReadEnvironment(),
            StandardInputSource.Empty(),
            OutputPolicy.Create(maximumOutputBytes, MaximumErrorBytes));
        var result = await _runner.RunAsync(invocation, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            var error = Encoding.UTF8.GetString(result.StandardError.Span).Trim();
            throw new GitCommandException(
                result.ExitCode,
                string.IsNullOrEmpty(error) ? fallbackError : error);
        }

        return result;
    }

    private static List<GitPath> ParsePaths(ReadOnlySpan<byte> bytes)
    {
        var paths = new List<GitPath>();
        while (!bytes.IsEmpty)
        {
            if (paths.Count >= MaximumPathCount)
            {
                throw new InvalidDataException("Git returned more untracked paths than the configured limit.");
            }

            var terminator = bytes.IndexOf((byte)0);
            if (terminator < 0)
            {
                throw new InvalidDataException("Git path enumeration ended before a NUL terminator.");
            }

            if (terminator == 0 || terminator > MaximumPathBytes)
            {
                throw new InvalidDataException("Git returned an empty or oversized untracked path.");
            }

            var field = bytes[..terminator];
            paths.Add(OperatingSystem.IsWindows()
                ? GitPath.FromWindowsPath(s_strictUtf8.GetString(field))
                : GitPath.FromUnixBytes(field));
            bytes = bytes[(terminator + 1)..];
        }

        return paths;
    }

    private static int GetBatchCount(List<GitPath> paths, int offset)
    {
        var count = 0;
        var units = 0;
        while (offset + count < paths.Count && count < MaximumBatchPaths)
        {
            var path = paths[offset + count];
            var pathUnits = path.Kind == NativePathKind.WindowsUtf16
                ? path.GetWindowsPath().Length
                : path.GetUnixBytes().Length;
            if (count > 0 && units + pathUnits > MaximumBatchArgumentUnits)
            {
                break;
            }

            units = checked(units + pathUnits);
            count++;
        }

        return count;
    }

    private static void ParseObjectIds(
        ReadOnlySpan<byte> bytes,
        int expectedCount,
        List<ObjectId> destination)
    {
        for (var index = 0; index < expectedCount; index++)
        {
            var terminator = bytes.IndexOf((byte)'\n');
            if (terminator < 0)
            {
                throw new InvalidDataException("Git hash-object ended before the expected object identifiers.");
            }

            var field = bytes[..terminator];
            if (!field.IsEmpty && field[^1] == (byte)'\r')
            {
                field = field[..^1];
            }

            if (!ObjectId.TryParseHex(field, out var objectId))
            {
                throw new InvalidDataException("Git hash-object returned an invalid object identifier.");
            }

            destination.Add(objectId!);
            bytes = bytes[(terminator + 1)..];
        }

        if (!bytes.IsEmpty)
        {
            throw new InvalidDataException("Git hash-object returned unexpected trailing output.");
        }
    }

    private static void AppendObjectIds(
        IncrementalHash hash,
        ReadOnlySpan<byte> label,
        List<ObjectId> objectIds)
    {
        AppendLength(hash, label.Length);
        hash.AppendData(label);
        AppendLength(hash, objectIds.Count);
        foreach (var objectId in objectIds)
        {
            AppendLength(hash, objectId.GetBytes().Length);
            hash.AppendData(objectId.GetBytes());
        }
    }

    private static void AppendSegment(
        IncrementalHash hash,
        ReadOnlySpan<byte> label,
        ReadOnlySpan<byte> bytes)
    {
        AppendLength(hash, label.Length);
        hash.AppendData(label);
        AppendLength(hash, bytes.Length);
        hash.AppendData(bytes);
    }

    private static void AppendLength(IncrementalHash hash, int length)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, length);
        hash.AppendData(bytes);
    }
}
