using GitSail.Domain;
using System.Collections.Immutable;
using System.Text;

namespace GitSail.Git.Execution;

/// <summary>
/// Reads and updates the bounded global recent-repository list through Git configuration.
/// </summary>
internal sealed class RecentRepositoryService
{
    private const int DefaultMaximumRecentRepositories = 10;
    private const int MaximumRecentRepositories = 100;
    private const int MaximumOutputBytes = 4 * 1024 * 1024;
    private const string MaximumConfigurationKey = "gui.maxrecentrepo";
    private const string RecentConfigurationKey = "gui.recentrepo";
    private static readonly UTF8Encoding s_strictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private readonly GitInstallation _installation;
    private readonly IChildProcessRunner _runner;
    private readonly GitChildEnvironmentFactory _environmentFactory;
    private readonly CanonicalDirectory _workingDirectory;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    /// <summary>
    /// Initializes recent-repository configuration over explicit Git and environment boundaries.
    /// </summary>
    /// <param name="installation">The resolved compatible Git installation.</param>
    /// <param name="runner">The sole shell-free child-process boundary.</param>
    /// <param name="environmentFactory">The operation-specific child-environment factory.</param>
    /// <param name="workingDirectory">The canonical non-repository-dependent command directory.</param>
    internal RecentRepositoryService(
        GitInstallation installation,
        IChildProcessRunner runner,
        GitChildEnvironmentFactory environmentFactory,
        CanonicalDirectory workingDirectory)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(environmentFactory);
        ArgumentNullException.ThrowIfNull(workingDirectory);
        _installation = installation;
        _runner = runner;
        _environmentFactory = environmentFactory;
        _workingDirectory = workingDirectory;
    }

    /// <summary>
    /// Loads newest-first exact native repository paths from the user's global Git configuration.
    /// </summary>
    /// <param name="cancellationToken">Signals configuration-read cancellation.</param>
    /// <returns>The configured number of distinct exact native repository paths.</returns>
    internal async Task<ImmutableArray<GitPath>> LoadAsync(CancellationToken cancellationToken)
    {
        var maximum = await LoadMaximumAsync(cancellationToken).ConfigureAwait(false);
        if (maximum == 0)
        {
            return [];
        }

        var result = await RunAsync(
            [
                ProcessArgument.Literal("--no-pager"),
                ProcessArgument.Literal("config"),
                ProcessArgument.Literal("--global"),
                ProcessArgument.Literal("--null"),
                ProcessArgument.Literal("--get-all"),
                ProcessArgument.Literal(RecentConfigurationKey),
            ],
            _environmentFactory.CreateConfigurationReadEnvironment(),
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode == 1)
        {
            return [];
        }

        ThrowIfFailed(result, "Recent repository loading");
        var oldestFirst = new List<GitPath>();
        var output = result.StandardOutput.Span;
        var offset = 0;
        while (offset < output.Length)
        {
            var terminator = output[offset..].IndexOf((byte)0);
            if (terminator < 0)
            {
                throw new InvalidDataException("Recent repository configuration has an unterminated value.");
            }

            var value = output.Slice(offset, terminator);
            offset += terminator + 1;
            if (value.IsEmpty)
            {
                continue;
            }

            var path = CreateNativePath(value);
            if (!oldestFirst.Any(existing => existing.Equals(path)))
            {
                oldestFirst.Add(path);
            }
        }

        return
        [
            .. oldestFirst
                .TakeLast(maximum)
                .Reverse(),
        ];
    }

    /// <summary>
    /// Moves one canonical repository path to the front and persists the bounded exact list through Git.
    /// </summary>
    /// <param name="directory">The successfully opened canonical repository worktree or bare directory.</param>
    /// <param name="cancellationToken">Signals configuration-write cancellation.</param>
    /// <returns>A task that completes after Git publishes the updated list.</returns>
    internal async Task RecordAsync(
        CanonicalDirectory directory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(directory);
        var path = CreateNativePath(directory);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await LoadAsync(cancellationToken).ConfigureAwait(false);
            var maximum = await LoadMaximumAsync(cancellationToken).ConfigureAwait(false);
            var updated = ImmutableArray.CreateBuilder<GitPath>(maximum);
            if (maximum > 0)
            {
                updated.Add(path);
            }

            foreach (var item in current)
            {
                if (updated.Count == maximum)
                {
                    break;
                }

                if (!item.Equals(path))
                {
                    updated.Add(item);
                }
            }

            await ReplaceAsync(updated, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// Removes one exact recent path without affecting any other global Git configuration value.
    /// </summary>
    /// <param name="path">The exact native recent path to remove.</param>
    /// <param name="cancellationToken">Signals configuration-write cancellation.</param>
    /// <returns>A task that completes after Git publishes the remaining list.</returns>
    internal async Task RemoveAsync(GitPath path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await LoadAsync(cancellationToken).ConfigureAwait(false);
            await ReplaceAsync(
                current.Where(item => !item.Equals(path)).ToImmutableArray(),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task ReplaceAsync(
        IEnumerable<GitPath> paths,
        CancellationToken cancellationToken)
    {
        var unsetResult = await RunAsync(
            [
                ProcessArgument.Literal("--no-pager"),
                ProcessArgument.Literal("config"),
                ProcessArgument.Literal("--global"),
                ProcessArgument.Literal("--unset-all"),
                ProcessArgument.Literal(RecentConfigurationKey),
            ],
            _environmentFactory.CreateRepositoryMutationEnvironment(),
            cancellationToken).ConfigureAwait(false);
        if (unsetResult.ExitCode is not 0 and not 5)
        {
            ThrowIfFailed(unsetResult, "Recent repository replacement");
        }

        foreach (var path in paths.Reverse())
        {
            var addResult = await RunAsync(
                [
                    ProcessArgument.Literal("--no-pager"),
                    ProcessArgument.Literal("config"),
                    ProcessArgument.Literal("--global"),
                    ProcessArgument.Literal("--add"),
                    ProcessArgument.Literal(RecentConfigurationKey),
                    ProcessArgument.Native(path),
                ],
                _environmentFactory.CreateRepositoryMutationEnvironment(),
                cancellationToken).ConfigureAwait(false);
            ThrowIfFailed(addResult, "Recent repository replacement");
        }
    }

    private async Task<int> LoadMaximumAsync(CancellationToken cancellationToken)
    {
        var result = await RunAsync(
            [
                ProcessArgument.Literal("--no-pager"),
                ProcessArgument.Literal("config"),
                ProcessArgument.Literal("--global"),
                ProcessArgument.Literal("--type=int"),
                ProcessArgument.Literal("--get"),
                ProcessArgument.Literal(MaximumConfigurationKey),
            ],
            _environmentFactory.CreateConfigurationReadEnvironment(),
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode == 1)
        {
            return DefaultMaximumRecentRepositories;
        }

        ThrowIfFailed(result, "Recent repository limit loading");
        var text = Encoding.ASCII.GetString(result.StandardOutput.Span).Trim();
        if (!int.TryParse(
            text,
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out var maximum) ||
            maximum is < 0 or > MaximumRecentRepositories)
        {
            throw new InvalidDataException(
                $"{MaximumConfigurationKey} must be an integer from 0 through {MaximumRecentRepositories}.");
        }

        return maximum;
    }

    private async Task<ProcessResult> RunAsync(
        ImmutableArray<ProcessArgument> arguments,
        ChildEnvironment environment,
        CancellationToken cancellationToken)
    {
        var invocation = new ProcessInvocation(
            _installation.Executable,
            arguments,
            _workingDirectory,
            environment,
            StandardInputSource.Empty(),
            OutputPolicy.Create(MaximumOutputBytes, MaximumOutputBytes));
        return await _runner.RunAsync(invocation, cancellationToken).ConfigureAwait(false);
    }

    private static void ThrowIfFailed(ProcessResult result, string operation)
    {
        if (result.ExitCode == 0)
        {
            return;
        }

        var error = Encoding.UTF8.GetString(result.StandardError.Span).Trim();
        throw new GitCommandException(
            result.ExitCode,
            error.Length == 0 ? $"{operation} failed with exit code {result.ExitCode}." : error);
    }

    private static GitPath CreateNativePath(ReadOnlySpan<byte> value)
        => OperatingSystem.IsWindows()
            ? GitPath.FromWindowsPath(s_strictUtf8.GetString(value))
            : GitPath.FromUnixBytes(value);

    private static GitPath CreateNativePath(CanonicalDirectory directory)
        => directory.Kind == NativePathKind.WindowsUtf16
            ? GitPath.FromWindowsPath(directory.GetWindowsPath())
            : GitPath.FromUnixBytes(directory.GetUnixBytes());
}
