using GitSail.Domain;
using System.Text;

namespace GitSail.Git.Execution;

/// <summary>
/// Selects and loads the highest-precedence recoverable, Git-created, amend, or configured message.
/// </summary>
internal sealed class CommitMessageInitializationService
{
    private const int MaximumCommitMessageBytes = 16 * 1024 * 1024;
    private static readonly UTF8Encoding s_strictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private readonly GitInstallation _installation;
    private readonly IChildProcessRunner _runner;
    private readonly GitChildEnvironmentFactory _environmentFactory;

    /// <summary>
    /// Initializes commit-message selection over bounded direct state reads and structured Git output.
    /// </summary>
    /// <param name="installation">The resolved Git installation.</param>
    /// <param name="runner">The sole child-process runner.</param>
    /// <param name="environmentFactory">The operation-specific child-environment factory.</param>
    internal CommitMessageInitializationService(
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
    }

    /// <summary>
    /// Loads recovery, operation state, amend text, or the effective template in exact precedence order.
    /// </summary>
    /// <param name="workingDirectory">The canonical repository working directory.</param>
    /// <param name="recoveryPaths">The ordered exact GitSail recovery-state paths.</param>
    /// <param name="mergeMessagePath">The Git-resolved merge-message path.</param>
    /// <param name="squashMessagePath">The Git-resolved squash-message path.</param>
    /// <param name="hasMergeHead">Whether Git's current worktree state has a pending merge parent.</param>
    /// <param name="amendHead">The exact commit selected for amend, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Signals message loading cancellation.</param>
    /// <returns>The selected UTF-8 editor message and its precedence source.</returns>
    internal async Task<CommitMessageInitialization> LoadAsync(
        CanonicalDirectory workingDirectory,
        IReadOnlyList<GitPath> recoveryPaths,
        GitPath mergeMessagePath,
        GitPath squashMessagePath,
        bool hasMergeHead,
        ObjectId? amendHead,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);
        ArgumentNullException.ThrowIfNull(recoveryPaths);
        ArgumentNullException.ThrowIfNull(mergeMessagePath);
        ArgumentNullException.ThrowIfNull(squashMessagePath);
        foreach (var path in recoveryPaths)
        {
            ArgumentNullException.ThrowIfNull(path);
            var recovered = await ReadStateMessageAsync(path, cancellationToken).ConfigureAwait(false);
            if (recovered is not null)
            {
                return new CommitMessageInitialization(
                    recovered,
                    CommitMessageInitializationKind.Recovery);
            }
        }

        if (hasMergeHead)
        {
            var mergeMessage = await ReadStateMessageAsync(
                mergeMessagePath,
                cancellationToken).ConfigureAwait(false);
            if (mergeMessage is not null)
            {
                return new CommitMessageInitialization(
                    mergeMessage,
                    CommitMessageInitializationKind.Merge);
            }
        }

        var squashMessage = await ReadStateMessageAsync(
            squashMessagePath,
            cancellationToken).ConfigureAwait(false);
        if (squashMessage is not null)
        {
            return new CommitMessageInitialization(
                squashMessage,
                CommitMessageInitializationKind.Squash);
        }

        if (amendHead is not null)
        {
            return new CommitMessageInitialization(
                await LoadAmendMessageAsync(
                        workingDirectory,
                        amendHead,
                        cancellationToken)
                    .ConfigureAwait(false),
                CommitMessageInitializationKind.Amend);
        }

        var template = await LoadTemplateAsync(workingDirectory, cancellationToken).ConfigureAwait(false);
        return template is null
            ? new CommitMessageInitialization(
                string.Empty,
                CommitMessageInitializationKind.Empty)
            : new CommitMessageInitialization(
                template,
                CommitMessageInitializationKind.Template);
    }

    private static async Task<string?> ReadStateMessageAsync(
        GitPath path,
        CancellationToken cancellationToken)
    {
        var bytes = await RepositoryStateFileSystem.ReadIfExistsAsync(
            path,
            MaximumCommitMessageBytes,
            cancellationToken).ConfigureAwait(false);
        return bytes is null ? null : Decode(bytes);
    }

    private async Task<string> LoadAmendMessageAsync(
        CanonicalDirectory workingDirectory,
        ObjectId commit,
        CancellationToken cancellationToken)
    {
        var invocation = new ProcessInvocation(
            _installation.Executable,
            [
                ProcessArgument.Literal("--no-pager"),
                ProcessArgument.Literal("log"),
                ProcessArgument.Literal("--max-count=1"),
                ProcessArgument.Literal("--encoding=UTF-8"),
                ProcessArgument.Literal("--format=format:%B"),
                ProcessArgument.Literal("--end-of-options"),
                ProcessArgument.Literal(commit.ToString()),
            ],
            workingDirectory,
            _environmentFactory.CreateRepositoryReadEnvironment(),
            StandardInputSource.Empty(),
            OutputPolicy.Create(MaximumCommitMessageBytes, 1024 * 1024));
        var result = await _runner.RunAsync(invocation, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            var error = Encoding.UTF8.GetString(result.StandardError.Span).Trim();
            throw new GitCommandException(
                result.ExitCode,
                string.IsNullOrEmpty(error) ? "Git could not load the amend commit message." : error);
        }

        return Decode(result.StandardOutput.Span);
    }

    private async Task<string?> LoadTemplateAsync(
        CanonicalDirectory workingDirectory,
        CancellationToken cancellationToken)
    {
        var invocation = new ProcessInvocation(
            _installation.Executable,
            [
                ProcessArgument.Literal("--no-pager"),
                ProcessArgument.Literal("config"),
                ProcessArgument.Literal("--null"),
                ProcessArgument.Literal("--type=path"),
                ProcessArgument.Literal("--get"),
                ProcessArgument.Literal("commit.template"),
            ],
            workingDirectory,
            _environmentFactory.CreateConfigurationReadEnvironment(),
            StandardInputSource.Empty(),
            OutputPolicy.Create(1024 * 1024, 1024 * 1024));
        var result = await _runner.RunAsync(invocation, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode == 1 && result.StandardOutput.IsEmpty)
        {
            return null;
        }

        if (result.ExitCode != 0)
        {
            var error = Encoding.UTF8.GetString(result.StandardError.Span).Trim();
            throw new GitCommandException(
                result.ExitCode,
                string.IsNullOrEmpty(error) ? "Git could not resolve the commit template." : error);
        }

        var configuredValue = ParseConfiguredPath(result.StandardOutput.Span);
        var configuredPath = ResolveConfiguredPath(workingDirectory, configuredValue);
        var bytes = await ConfiguredFileReader.ReadIfExistsAsync(
            configuredPath,
            MaximumCommitMessageBytes,
            cancellationToken).ConfigureAwait(false);
        if (bytes is null)
        {
            throw new FileNotFoundException(
                $"The configured commit template does not exist: {configuredPath.DisplayText}");
        }

        return Decode(bytes);
    }

    private static ReadOnlySpan<byte> ParseConfiguredPath(ReadOnlySpan<byte> output)
    {
        if (output.Length < 2 || output[^1] != 0)
        {
            throw new InvalidDataException("Git returned an invalid configured commit-template path.");
        }

        var value = output[..^1];
        if (value.Contains((byte)0))
        {
            throw new InvalidDataException("Git returned multiple configured commit-template paths.");
        }

        return value;
    }

    private static GitPath ResolveConfiguredPath(
        CanonicalDirectory workingDirectory,
        ReadOnlySpan<byte> configuredValue)
    {
        if (OperatingSystem.IsWindows())
        {
            string configuredPath;
            try
            {
                configuredPath = s_strictUtf8.GetString(configuredValue);
            }
            catch (DecoderFallbackException exception)
            {
                throw new InvalidDataException(
                    "The configured commit-template path is not valid UTF-8.",
                    exception);
            }

            return GitPath.FromWindowsPath(Path.GetFullPath(
                configuredPath,
                workingDirectory.GetWindowsPath()));
        }

        if (configuredValue[0] == (byte)'/')
        {
            return GitPath.FromUnixBytes(configuredValue);
        }

        var directory = workingDirectory.GetUnixBytes();
        var separatorLength = directory[^1] == (byte)'/' ? 0 : 1;
        var absolutePath = new byte[directory.Length + separatorLength + configuredValue.Length];
        directory.CopyTo(absolutePath);
        if (separatorLength != 0)
        {
            absolutePath[directory.Length] = (byte)'/';
        }

        configuredValue.CopyTo(absolutePath.AsSpan(directory.Length + separatorLength));
        return GitPath.FromUnixBytes(absolutePath);
    }

    private static string Decode(ReadOnlySpan<byte> bytes)
    {
        try
        {
            var message = s_strictUtf8.GetString(bytes);
            if (message.Contains('\0', StringComparison.Ordinal))
            {
                throw new InvalidDataException("A commit message contains NUL.");
            }

            return message;
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("A commit message is not valid UTF-8.", exception);
        }
    }
}
