using GitSail.Domain;
using System.Collections.Immutable;
using System.Text;

namespace GitSail.Git.Execution;

/// <summary>
/// Reads and updates the bounded global recent-repository list through Git configuration.
/// </summary>
internal sealed class RecentRepositoryService
{
    private const int MaximumRecentRepositories = 20;
    private const int MaximumOutputBytes = 4 * 1024 * 1024;
    private const string ConfigurationKey = "gitsail.recentRepositories";
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
    /// <returns>At most twenty distinct exact native repository paths.</returns>
    internal async Task<ImmutableArray<GitPath>> LoadAsync(CancellationToken cancellationToken)
    {
        var result = await RunAsync(
            [
                ProcessArgument.Literal("--no-pager"),
                ProcessArgument.Literal("config"),
                ProcessArgument.Literal("--global"),
                ProcessArgument.Literal("--null"),
                ProcessArgument.Literal("--get-all"),
                ProcessArgument.Literal(ConfigurationKey),
            ],
            _environmentFactory.CreateConfigurationReadEnvironment(),
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode == 1)
        {
            return [];
        }

        ThrowIfFailed(result, "Recent repository loading");
        var paths = ImmutableArray.CreateBuilder<GitPath>();
        var output = result.StandardOutput.Span;
        var offset = 0;
        while (offset < output.Length && paths.Count < MaximumRecentRepositories)
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
            if (!paths.Any(existing => existing.Equals(path)))
            {
                paths.Add(path);
            }
        }

        return paths.ToImmutable();
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
            var updated = ImmutableArray.CreateBuilder<GitPath>(MaximumRecentRepositories);
            updated.Add(path);
            foreach (var item in current)
            {
                if (updated.Count == MaximumRecentRepositories)
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
                ProcessArgument.Literal(ConfigurationKey),
            ],
            _environmentFactory.CreateRepositoryMutationEnvironment(),
            cancellationToken).ConfigureAwait(false);
        if (unsetResult.ExitCode is not 0 and not 5)
        {
            ThrowIfFailed(unsetResult, "Recent repository replacement");
        }

        foreach (var path in paths)
        {
            var addResult = await RunAsync(
                [
                    ProcessArgument.Literal("--no-pager"),
                    ProcessArgument.Literal("config"),
                    ProcessArgument.Literal("--global"),
                    ProcessArgument.Literal("--add"),
                    ProcessArgument.Literal(ConfigurationKey),
                    ProcessArgument.Native(path),
                ],
                _environmentFactory.CreateRepositoryMutationEnvironment(),
                cancellationToken).ConfigureAwait(false);
            ThrowIfFailed(addResult, "Recent repository replacement");
        }
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
