using GitSail.Domain;
using System.Collections.Immutable;
using System.Text;

namespace GitSail.Git.Execution;

/// <summary>
/// Executes canonical shell-free repository initialization and clone transactions for the chooser.
/// </summary>
internal sealed class RepositoryManagementService
{
    private const int MaximumOutputBytes = 16 * 1024 * 1024;
    private readonly GitInstallation _installation;
    private readonly IChildProcessRunner _runner;
    private readonly GitChildEnvironmentFactory _environmentFactory;
    private readonly CredentialPromptBroker _credentialPromptBroker;
    private readonly CanonicalDirectory _launchDirectory;
    private readonly RepositoryTargetPlanner _targetPlanner;

    /// <summary>
    /// Initializes chooser repository management over explicit Git, process, environment, credential, and launch-directory boundaries.
    /// </summary>
    /// <param name="installation">The resolved compatible Git installation.</param>
    /// <param name="runner">The sole shell-free child-process boundary.</param>
    /// <param name="environmentFactory">The operation-specific child-environment factory.</param>
    /// <param name="credentialPromptBroker">The authenticated one-operation credential prompt broker.</param>
    /// <param name="launchDirectory">The canonical directory used for safe relative-path prefill and resolution.</param>
    internal RepositoryManagementService(
        GitInstallation installation,
        IChildProcessRunner runner,
        GitChildEnvironmentFactory environmentFactory,
        CredentialPromptBroker credentialPromptBroker,
        CanonicalDirectory launchDirectory)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(environmentFactory);
        ArgumentNullException.ThrowIfNull(credentialPromptBroker);
        ArgumentNullException.ThrowIfNull(launchDirectory);
        _installation = installation;
        _runner = runner;
        _environmentFactory = environmentFactory;
        _credentialPromptBroker = credentialPromptBroker;
        _launchDirectory = launchDirectory;
        _targetPlanner = new RepositoryTargetPlanner(launchDirectory);
    }

    /// <summary>
    /// Gets the compatible Git installation used by every chooser operation.
    /// </summary>
    internal GitInstallation Installation => _installation;

    /// <summary>
    /// Canonicalizes one target before creation without creating any filesystem entry.
    /// </summary>
    /// <param name="targetDirectory">The absolute or launch-directory-relative user input.</param>
    /// <returns>The exact target, existing canonical parent, and pre-operation existence state.</returns>
    internal RepositoryTargetPlan PrepareTarget(string targetDirectory)
        => _targetPlanner.Prepare(targetDirectory);

    /// <summary>
    /// Initializes a normal or bare repository at one canonical planned target.
    /// </summary>
    /// <param name="targetDirectory">The absolute or launch-directory-relative target input.</param>
    /// <param name="bare">Whether Git creates a repository without a worktree.</param>
    /// <param name="cancellationToken">Signals process-tree cancellation.</param>
    /// <returns>The canonical created repository directory and exact bounded Git output.</returns>
    internal async Task<RepositoryCreationResult> InitializeAsync(
        string targetDirectory,
        bool bare,
        CancellationToken cancellationToken)
    {
        var plan = PrepareTarget(targetDirectory);
        var arguments = ImmutableArray.CreateBuilder<ProcessArgument>();
        arguments.Add(ProcessArgument.Literal("--no-pager"));
        arguments.Add(ProcessArgument.Literal("init"));
        arguments.Add(ProcessArgument.Literal("--quiet"));
        if (bare)
        {
            arguments.Add(ProcessArgument.Literal("--bare"));
        }

        arguments.Add(ProcessArgument.Literal("--"));
        arguments.Add(ProcessArgument.Native(plan.TargetPath));
        ProcessResult result;
        try
        {
            result = await RunAsync(
                arguments.ToImmutable(),
                _environmentFactory.CreateRepositoryMutationEnvironment(),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new RepositoryCreationCancelledException(
                CreatedDirectoryCleanup.Capture(plan),
                cancellationToken);
        }

        ThrowIfFailed(result, plan, bare ? "Bare repository initialization" : "Repository initialization");
        return new RepositoryCreationResult(
            CanonicalDirectory.Create(plan.ManagedTargetPath),
            new GitOperationResult(result.StandardOutput, result.StandardError),
            bare);
    }

    /// <summary>
    /// Clones one literal source into a canonical target with the selected Git-owned object and submodule behavior.
    /// </summary>
    /// <param name="request">The validated source, target, local-object mode, and recursive-submodule choice.</param>
    /// <param name="cancellationToken">Signals process-tree cancellation.</param>
    /// <returns>The canonical cloned worktree and exact bounded Git output.</returns>
    internal async Task<RepositoryCreationResult> CloneAsync(
        RepositoryCloneRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var plan = PrepareTarget(request.TargetDirectory);
        var arguments = CreateCloneArguments(request, plan.TargetPath);
        await using var credentialOperation = _credentialPromptBroker.StartOperation(
            "Clone repository",
            cancellationToken);
        var environment = credentialOperation.ConfigureEnvironment(
            _environmentFactory.CreateTransportEnvironment());
        ProcessResult result;
        try
        {
            result = await RunAsync(arguments, environment, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new RepositoryCreationCancelledException(
                CreatedDirectoryCleanup.Capture(plan),
                cancellationToken);
        }

        ThrowIfFailed(result, plan, "Repository clone");
        return new RepositoryCreationResult(
            CanonicalDirectory.Create(plan.ManagedTargetPath),
            new GitOperationResult(result.StandardOutput, result.StandardError),
            isBare: false);
    }

    /// <summary>
    /// Builds the complete ordered clone argument contract without parsing user input as command syntax.
    /// </summary>
    /// <param name="request">The validated chooser request.</param>
    /// <param name="targetPath">The canonical native target path.</param>
    /// <returns>Literal Git arguments with an option terminator before source and target operands.</returns>
    internal static ImmutableArray<ProcessArgument> CreateCloneArguments(
        RepositoryCloneRequest request,
        GitPath targetPath)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(targetPath);
        var arguments = ImmutableArray.CreateBuilder<ProcessArgument>();
        arguments.Add(ProcessArgument.Literal("--no-pager"));
        arguments.Add(ProcessArgument.Literal("clone"));
        arguments.Add(ProcessArgument.Literal("--progress"));
        switch (request.Mode)
        {
            case RepositoryCloneMode.Standard:
                break;
            case RepositoryCloneMode.FullCopy:
                arguments.Add(ProcessArgument.Literal("--no-hardlinks"));
                break;
            case RepositoryCloneMode.Shared:
                arguments.Add(ProcessArgument.Literal("--shared"));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(request), request.Mode, "Unknown clone mode.");
        }

        if (request.RecurseSubmodules)
        {
            arguments.Add(ProcessArgument.Literal("--recurse-submodules"));
        }

        arguments.Add(ProcessArgument.Literal("--"));
        arguments.Add(ProcessArgument.Literal(request.Source));
        arguments.Add(ProcessArgument.Native(targetPath));
        return arguments.ToImmutable();
    }

    private async Task<ProcessResult> RunAsync(
        ImmutableArray<ProcessArgument> arguments,
        ChildEnvironment environment,
        CancellationToken cancellationToken)
    {
        var invocation = new ProcessInvocation(
            _installation.Executable,
            arguments,
            _launchDirectory,
            environment,
            StandardInputSource.Empty(),
            OutputPolicy.Create(MaximumOutputBytes, MaximumOutputBytes));
        return await _runner.RunAsync(invocation, cancellationToken).ConfigureAwait(false);
    }

    private static void ThrowIfFailed(
        ProcessResult result,
        RepositoryTargetPlan plan,
        string operation)
    {
        if (result.ExitCode == 0)
        {
            return;
        }

        var error = Encoding.UTF8.GetString(result.StandardError.Span).Trim();
        if (error.Length == 0)
        {
            error = Encoding.UTF8.GetString(result.StandardOutput.Span).Trim();
        }

        throw new RepositoryCreationException(
            result.ExitCode,
            error.Length == 0 ? $"{operation} failed with exit code {result.ExitCode}." : error,
            CreatedDirectoryCleanup.Capture(plan));
    }

}
