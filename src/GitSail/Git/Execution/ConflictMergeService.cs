using GitSail.Domain;
using GitSail.Git.Parsing;
using System.Security.Cryptography;
using System.Text;

namespace GitSail.Git.Execution;

/// <summary>
/// Delegates three-way file merging to Git and indexes collision-checked raw conflict chunks.
/// </summary>
internal sealed class ConflictMergeService
{
    private const int InitialMarkerSize = 32;
    private const int MaximumMarkerSize = 4096;
    private const int SpoolMemoryThresholdBytes = 1024 * 1024;
    private const int MaximumMergeBytes = 1024 * 1024 * 1024;
    private const int MaximumErrorBytes = 4 * 1024 * 1024;
    private readonly GitInstallation _installation;
    private readonly IChildProcessRunner _runner;
    private readonly GitChildEnvironmentFactory _environmentFactory;
    private readonly IProcessEnvironment _environment;

    /// <summary>
    /// Initializes three-way conflict merging over the sole typed child-process boundary.
    /// </summary>
    /// <param name="installation">The resolved Git installation.</param>
    /// <param name="runner">The sole child-process runner.</param>
    /// <param name="environmentFactory">The operation-specific child-environment factory.</param>
    /// <param name="environment">The classified process environment used for private cache paths.</param>
    internal ConflictMergeService(
        GitInstallation installation,
        IChildProcessRunner runner,
        GitChildEnvironmentFactory environmentFactory,
        IProcessEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(environmentFactory);
        ArgumentNullException.ThrowIfNull(environment);
        _installation = installation;
        _runner = runner;
        _environmentFactory = environmentFactory;
        _environment = environment;
    }

    /// <summary>
    /// Produces a raw diff3 merge document from exact immutable base, ours, and theirs stages.
    /// </summary>
    /// <param name="workingDirectory">The canonical repository working directory.</param>
    /// <param name="contents">The exact optional base, ours, and theirs stage content.</param>
    /// <param name="cancellationToken">Signals Git merge cancellation.</param>
    /// <returns>The exact merge bytes and validated conflict-chunk ranges.</returns>
    internal async Task<ConflictMergeDocument> MergeAsync(
        CanonicalDirectory workingDirectory,
        ConflictStageContents contents,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);
        ArgumentNullException.ThrowIfNull(contents);
        ValidateBlobBacked(contents.Base);
        ValidateBlobBacked(contents.Ours);
        ValidateBlobBacked(contents.Theirs);
        var baseContent = contents.Base?.Content?.ToArray() ?? [];
        var oursContent = contents.Ours?.Content?.ToArray() ?? [];
        var theirsContent = contents.Theirs?.Content?.ToArray() ?? [];
        if (baseContent.AsSpan().Contains((byte)0) ||
            oursContent.AsSpan().Contains((byte)0) ||
            theirsContent.AsSpan().Contains((byte)0))
        {
            throw new InvalidDataException(
                "Per-hunk resolution is unavailable because at least one conflict stage is binary.");
        }

        var markers = CreateMarkers(baseContent, oursContent, theirsContent);
        ProcessResult? mergeResult = null;
        if (contents.Base is not null && contents.Ours is not null && contents.Theirs is not null)
        {
            mergeResult = await RunObjectMergeAsync(
                workingDirectory,
                contents,
                markers,
                cancellationToken).ConfigureAwait(false);
            if (mergeResult.ExitCode > 127)
            {
                mergeResult.StandardOutputSpool?.Dispose();
                mergeResult = null;
            }
        }

        mergeResult ??= await RunFileMergeAsync(
            workingDirectory,
            oursContent,
            baseContent,
            theirsContent,
            markers,
            cancellationToken).ConfigureAwait(false);
        using var spool = mergeResult.StandardOutputSpool
            ?? throw new InvalidOperationException("Conflict merging did not return its required byte spool.");
        if (mergeResult.ExitCode > 127)
        {
            var error = Encoding.UTF8.GetString(mergeResult.StandardError.Span).Trim();
            throw new GitCommandException(
                mergeResult.ExitCode,
                string.IsNullOrEmpty(error) ? "Git could not perform the three-way file merge." : error);
        }

        if (spool.Length > int.MaxValue)
        {
            throw new InvalidDataException("Three-way merge output exceeds the supported in-memory length.");
        }

        var mergedBytes = await spool.ReadSliceAsync(
            offset: 0,
            checked((int)spool.Length),
            cancellationToken).ConfigureAwait(false);
        var document = ConflictMarkerParser.Parse(mergedBytes, markers);
        if ((mergeResult.ExitCode < 127 && document.Chunks.Length != mergeResult.ExitCode) ||
            (mergeResult.ExitCode == 127 && document.Chunks.Length < 127))
        {
            throw new InvalidDataException("Git's reported conflict count does not match its merge markers.");
        }

        return document;
    }

    private async Task<ProcessResult> RunObjectMergeAsync(
        CanonicalDirectory workingDirectory,
        ConflictStageContents contents,
        ConflictMarkerSet markers,
        CancellationToken cancellationToken)
    {
        var arguments = CreateCommonArguments(markers);
        arguments.Add(ProcessArgument.Literal("--object-id"));
        arguments.Add(ProcessArgument.Literal(contents.Ours!.Stage.ObjectId.ToString()));
        arguments.Add(ProcessArgument.Literal(contents.Base!.Stage.ObjectId.ToString()));
        arguments.Add(ProcessArgument.Literal(contents.Theirs!.Stage.ObjectId.ToString()));
        return await RunAsync(workingDirectory, arguments, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ProcessResult> RunFileMergeAsync(
        CanonicalDirectory workingDirectory,
        ReadOnlyMemory<byte> ours,
        ReadOnlyMemory<byte> @base,
        ReadOnlyMemory<byte> theirs,
        ConflictMarkerSet markers,
        CancellationToken cancellationToken)
    {
        var mergeCacheDirectory = Path.Combine(
            new UserDirectoryPathService(_environment).GetCacheDirectory(),
            "merge");
        UserDirectoryFileSystem.EnsurePrivateDirectory(mergeCacheDirectory);
        var temporaryDirectory = Path.Combine(
            mergeCacheDirectory,
            $"gitsail-conflict-{Guid.NewGuid():N}");
        CreatePrivateDirectory(temporaryDirectory);
        var oursPath = Path.Combine(temporaryDirectory, "ours");
        var basePath = Path.Combine(temporaryDirectory, "base");
        var theirsPath = Path.Combine(temporaryDirectory, "theirs");
        try
        {
            await WritePrivateFileAsync(oursPath, ours, cancellationToken).ConfigureAwait(false);
            await WritePrivateFileAsync(basePath, @base, cancellationToken).ConfigureAwait(false);
            await WritePrivateFileAsync(theirsPath, theirs, cancellationToken).ConfigureAwait(false);
            var arguments = CreateCommonArguments(markers);
            arguments.Add(ProcessArgument.Literal(oursPath));
            arguments.Add(ProcessArgument.Literal(basePath));
            arguments.Add(ProcessArgument.Literal(theirsPath));
            return await RunAsync(workingDirectory, arguments, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            TryDeleteFile(oursPath);
            TryDeleteFile(basePath);
            TryDeleteFile(theirsPath);
            try
            {
                Directory.Delete(temporaryDirectory);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }

    private Task<ProcessResult> RunAsync(
        CanonicalDirectory workingDirectory,
        IReadOnlyList<ProcessArgument> arguments,
        CancellationToken cancellationToken)
    {
        var invocation = new ProcessInvocation(
            _installation.Executable,
            [.. arguments],
            workingDirectory,
            _environmentFactory.CreateRepositoryReadEnvironment(),
            StandardInputSource.Empty(),
            OutputPolicy.CreateSpooling(
                SpoolMemoryThresholdBytes,
                MaximumMergeBytes,
                MaximumErrorBytes));
        return _runner.RunAsync(invocation, cancellationToken);
    }

    private static List<ProcessArgument> CreateCommonArguments(ConflictMarkerSet markers)
        =>
        [
            ProcessArgument.Literal("--no-pager"),
            ProcessArgument.Literal("merge-file"),
            ProcessArgument.Literal("--stdout"),
            ProcessArgument.Literal("--quiet"),
            ProcessArgument.Literal("--diff3"),
            ProcessArgument.Literal($"--marker-size={markers.MarkerSize}"),
            ProcessArgument.Literal("-L"),
            ProcessArgument.Literal(markers.OursLabel),
            ProcessArgument.Literal("-L"),
            ProcessArgument.Literal(markers.BaseLabel),
            ProcessArgument.Literal("-L"),
            ProcessArgument.Literal(markers.TheirsLabel),
        ];

    private static ConflictMarkerSet CreateMarkers(
        ReadOnlySpan<byte> @base,
        ReadOnlySpan<byte> ours,
        ReadOnlySpan<byte> theirs)
    {
        var markerSize = InitialMarkerSize;
        while (markerSize <= MaximumMarkerSize &&
            (ContainsSeparatorLine(@base, markerSize) ||
                ContainsSeparatorLine(ours, markerSize) ||
                ContainsSeparatorLine(theirs, markerSize)))
        {
            markerSize++;
        }

        if (markerSize > MaximumMarkerSize)
        {
            throw new InvalidDataException("Conflict content exhausts the safe marker-width range.");
        }

        for (var attempt = 0; attempt < 16; attempt++)
        {
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            var markers = new ConflictMarkerSet(markerSize, token);
            if (!ContainsLabeledMarker(@base, markers) &&
                !ContainsLabeledMarker(ours, markers) &&
                !ContainsLabeledMarker(theirs, markers))
            {
                return markers;
            }
        }

        throw new InvalidDataException("Conflict content repeatedly collided with unique merge labels.");
    }

    private static bool ContainsSeparatorLine(ReadOnlySpan<byte> content, int markerSize)
    {
        var offset = 0;
        while (offset < content.Length)
        {
            var relativeEnd = content[offset..].IndexOf((byte)'\n');
            var end = relativeEnd < 0 ? content.Length : offset + relativeEnd;
            if (end > offset && content[end - 1] == (byte)'\r')
            {
                end--;
            }

            var line = content[offset..end];
            if (line.Length == markerSize && line.IndexOfAnyExcept((byte)'=') < 0)
            {
                return true;
            }

            if (relativeEnd < 0)
            {
                break;
            }

            offset += relativeEnd + 1;
        }

        return false;
    }

    private static bool ContainsLabeledMarker(
        ReadOnlySpan<byte> content,
        ConflictMarkerSet markers)
        => ContainsExactLine(content, markers.OpeningMarker) ||
            ContainsExactLine(content, markers.BaseMarker) ||
            ContainsExactLine(content, markers.ClosingMarker);

    private static bool ContainsExactLine(ReadOnlySpan<byte> content, ReadOnlySpan<byte> expected)
    {
        var offset = 0;
        while (offset < content.Length)
        {
            var relativeEnd = content[offset..].IndexOf((byte)'\n');
            var end = relativeEnd < 0 ? content.Length : offset + relativeEnd;
            if (end > offset && content[end - 1] == (byte)'\r')
            {
                end--;
            }

            if (content[offset..end].SequenceEqual(expected))
            {
                return true;
            }

            if (relativeEnd < 0)
            {
                break;
            }

            offset += relativeEnd + 1;
        }

        return false;
    }

    private static void ValidateBlobBacked(ConflictStageContent? stage)
    {
        if (stage is not null && stage.Content is null)
        {
            throw new InvalidDataException("Per-hunk resolution is unavailable for a submodule conflict.");
        }
    }

    private static void CreatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
        }
        else
        {
            _ = Directory.CreateDirectory(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static async Task WritePrivateFileAsync(
        string path,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            BufferSize = 64 * 1024,
            Options = FileOptions.Asynchronous | FileOptions.WriteThrough,
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        await using var stream = new FileStream(path, options);
        await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (FileNotFoundException)
        {
        }
    }
}
