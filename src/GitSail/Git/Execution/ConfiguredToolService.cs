using GitSail.Domain;
using System.Buffers;
using System.Collections.Immutable;
using System.Text;

namespace GitSail.Git.Execution;

/// <summary>
/// Reviews and runs user-defined Git GUI tools through the fixed platform shell.
/// </summary>
internal sealed class ConfiguredToolService
{
    private const int MaximumFramedEnvironmentBytes = 1024 * 1024;
    private const int MaximumWindowsEnvironmentBlockCharacters = 32_767;
    private const int MaximumWindowsShellCommandCharacters = 24 * 1024;
    private const int MaximumStandardOutputBytes = 4 * 1024 * 1024;
    private const int MaximumStandardErrorBytes = 4 * 1024 * 1024;
    private static readonly UTF8Encoding s_utf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private readonly IChildProcessRunner _runner;
    private readonly GitChildEnvironmentFactory _environmentFactory;
    private readonly RepositoryMutationCoordinator _mutationCoordinator;
    private readonly ExecutableConfigurationBroker _broker;
    private readonly ResolvedExecutable _shell;

    /// <summary>
    /// Initializes configured-tool execution over the sole process and capability boundaries.
    /// </summary>
    /// <param name="runner">The sole bounded child-process runner.</param>
    /// <param name="environmentFactory">The operation-specific child-environment factory.</param>
    /// <param name="mutationCoordinator">The repository mutation serializer.</param>
    /// <param name="broker">The executable-configuration authorization boundary.</param>
    /// <param name="shell">The fixed resolved platform shell.</param>
    internal ConfiguredToolService(
        IChildProcessRunner runner,
        GitChildEnvironmentFactory environmentFactory,
        RepositoryMutationCoordinator mutationCoordinator,
        ExecutableConfigurationBroker broker,
        ResolvedExecutable shell)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(environmentFactory);
        ArgumentNullException.ThrowIfNull(mutationCoordinator);
        ArgumentNullException.ThrowIfNull(broker);
        ArgumentNullException.ThrowIfNull(shell);
        if (shell.Kind != ProgramKind.Shell)
        {
            throw new ArgumentException(
                "Configured tools require the fixed resolved platform shell.",
                nameof(shell));
        }

        _runner = runner;
        _environmentFactory = environmentFactory;
        _mutationCoordinator = mutationCoordinator;
        _broker = broker;
        _shell = shell;
    }

    /// <summary>
    /// Reviews and runs one exact configured tool with a captured repository input snapshot.
    /// </summary>
    /// <param name="workingDirectory">The canonical repository working directory.</param>
    /// <param name="tool">The exact effective tool configuration.</param>
    /// <param name="input">The bounded exact selected repository values.</param>
    /// <param name="cancellationToken">Signals review, lease acquisition, and child cancellation.</param>
    /// <returns>The denied or bounded completed tool result.</returns>
    internal async Task<ConfiguredToolResult> RunAsync(
        CanonicalDirectory workingDirectory,
        ConfiguredToolDefinition tool,
        ConfiguredToolInvocation input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(input);
        if (!tool.IsAvailable || tool.Command is null)
        {
            throw new InvalidOperationException(
                tool.UnavailableReason ?? "The configured tool is unavailable.");
        }

        if (OperatingSystem.IsWindows() &&
            tool.Command.Length > MaximumWindowsShellCommandCharacters)
        {
            throw new InvalidDataException(
                "The configured tool command exceeds the Windows command-line transport limit.");
        }

        if (tool.NeedsFile && input.FocusedPath is null)
        {
            throw new InvalidOperationException(
                $"Running configured tool '{tool.Name}' requires a focused path.");
        }

        var exposures = CreateExposureDescriptions(input);
        var request = new ExecutableCapabilityRequest(
            GitConfigurationExecutionKind.Tool,
            tool.ConfigurationKey,
            tool.SourceScope,
            tool.SourceOrigin,
            tool.Command,
            _shell,
            workingDirectory,
            usesShell: true,
            exposures);
        if (!await _broker.AuthorizeAsync(request, cancellationToken).ConfigureAwait(false))
        {
            return new ConfiguredToolResult(
                ConfiguredToolOutcome.Denied,
                ExitCode: null,
                StandardOutput: ReadOnlyMemory<byte>.Empty,
                StandardError: ReadOnlyMemory<byte>.Empty,
                Duration: TimeSpan.Zero);
        }

        var environment = CreateEnvironment(tool, input);
        var invocation = new ProcessInvocation(
            _shell,
            CreateShellArguments(tool.Command),
            workingDirectory,
            environment,
            StandardInputSource.Empty(),
            OutputPolicy.Create(MaximumStandardOutputBytes, MaximumStandardErrorBytes));
        await using var lease = await _mutationCoordinator.AcquireAsync(
            RepositoryMutationPurpose.Tool,
            cancellationToken).ConfigureAwait(false);
        var result = await _runner.RunAsync(invocation, cancellationToken).ConfigureAwait(false);
        return new ConfiguredToolResult(
            result.ExitCode == 0
                ? ConfiguredToolOutcome.Succeeded
                : ConfiguredToolOutcome.Failed,
            result.ExitCode,
            result.StandardOutput,
            result.StandardError,
            result.Duration);
    }

    private ChildEnvironment CreateEnvironment(
        ConfiguredToolDefinition tool,
        ConfiguredToolInvocation input)
    {
        var environment = _environmentFactory.CreateToolEnvironment()
            .SetValue("GIT_GUITOOL", tool.Name)
            .SetValue("CUR_BRANCH", string.Empty)
            .SetValue("FILENAME", string.Empty)
            .SetValue("FILENAMES", string.Empty)
            .SetValue("GITSAIL_FILENAME", string.Empty)
            .SetValue("GITSAIL_FILENAMES", string.Empty)
            .SetValue("GITSAIL_FILENAMES_ENCODING", OperatingSystem.IsWindows()
                ? "windows-utf16le-base64url-lines-v1"
                : "unix-bytes-base64url-lines-v1");
        if (input.CurrentBranch is { } branch)
        {
            environment = SetNativeValue(environment, "CUR_BRANCH", branch.GetBytes());
        }

        if (input.FocusedPath is { } focusedPath)
        {
            environment = SetPathValue(environment, "FILENAME", focusedPath)
                .SetValue("GITSAIL_FILENAME", EncodePath(focusedPath));
        }

        if (!input.SelectedPaths.IsEmpty)
        {
            environment = SetSelectedPaths(environment, input.SelectedPaths);
        }

        if (input.Arguments is { } arguments)
        {
            environment = environment.SetValue("ARGS", arguments);
        }

        if (input.Revision is { } revision)
        {
            environment = environment.SetValue("REVISION", revision);
        }

        if (OperatingSystem.IsWindows() &&
            environment.GetWindowsEnvironmentBlockCharacterCount() >
                MaximumWindowsEnvironmentBlockCharacters)
        {
            throw new InvalidDataException(
                "The configured-tool environment exceeds the Windows process transport limit.");
        }

        return environment;
    }

    private static ChildEnvironment SetSelectedPaths(
        ChildEnvironment environment,
        ImmutableArray<GitPath> paths)
    {
        var framed = string.Join('\n', paths.Select(EncodePath));
        if (s_utf8.GetByteCount(framed) > MaximumFramedEnvironmentBytes)
        {
            throw new InvalidDataException(
                "The selected paths exceed the configured-tool environment transport limit.");
        }

        environment = environment.SetValue("GITSAIL_FILENAMES", framed);
        if (OperatingSystem.IsWindows())
        {
            return environment.SetValue(
                "FILENAMES",
                string.Join('\n', paths.Select(static path => path.GetWindowsPath())));
        }

        var writer = new ArrayBufferWriter<byte>();
        foreach (var path in paths)
        {
            if (writer.WrittenCount > 0)
            {
                writer.Write([(byte)'\n']);
            }

            writer.Write(path.GetUnixBytes());
        }

        if (writer.WrittenCount > MaximumFramedEnvironmentBytes)
        {
            throw new InvalidDataException(
                "The selected paths exceed the configured-tool environment transport limit.");
        }

        return environment.SetUnixValue("FILENAMES", writer.WrittenSpan);
    }

    private static ChildEnvironment SetPathValue(
        ChildEnvironment environment,
        string name,
        GitPath path)
        => OperatingSystem.IsWindows()
            ? environment.SetValue(name, path.GetWindowsPath())
            : environment.SetUnixValue(name, path.GetUnixBytes());

    private static ChildEnvironment SetNativeValue(
        ChildEnvironment environment,
        string name,
        ReadOnlySpan<byte> value)
        => OperatingSystem.IsWindows()
            ? environment.SetValue(name, s_utf8.GetString(value))
            : environment.SetUnixValue(name, value);

    private static string EncodePath(GitPath path)
    {
        var bytes = path.Kind == NativePathKind.WindowsUtf16
            ? Encoding.Unicode.GetBytes(path.GetWindowsPath())
            : path.GetUnixBytes().ToArray();
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static ImmutableArray<string> CreateExposureDescriptions(
        ConfiguredToolInvocation input)
    {
        var exposures = ImmutableArray.CreateBuilder<string>();
        exposures.Add("configured tool name");
        if (input.FocusedPath is not null)
        {
            exposures.Add("focused repository path");
        }

        if (!input.SelectedPaths.IsEmpty)
        {
            exposures.Add("selected repository paths");
        }

        if (input.CurrentBranch is not null)
        {
            exposures.Add("current branch name");
        }

        if (input.Arguments is not null)
        {
            exposures.Add("entered arguments");
        }

        if (input.Revision is not null)
        {
            exposures.Add("entered revision");
        }

        return exposures.ToImmutable();
    }

    private static ImmutableArray<ProcessArgument> CreateShellArguments(string command)
        => OperatingSystem.IsWindows()
            ?
            [
                ProcessArgument.Literal("/d"),
                ProcessArgument.Literal("/s"),
                ProcessArgument.Literal("/c"),
                ProcessArgument.Literal(command),
            ]
            :
            [
                ProcessArgument.Literal("-c"),
                ProcessArgument.Literal(command),
            ];
}
