using GitSail.Domain;
using GitSail.Git.Parsing;
using System.Collections.Immutable;
using System.Text;

namespace GitSail.Git.Execution;

/// <summary>
/// Loads explicit Git configuration values with their scope and source file.
/// </summary>
internal sealed class GitConfigurationService
{
    private static readonly UTF8Encoding s_strictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private readonly GitInstallation _installation;
    private readonly IChildProcessRunner _runner;
    private readonly GitChildEnvironmentFactory _environmentFactory;
    private readonly GitConfigurationParser _parser;
    private readonly RepositoryMutationCoordinator? _mutationCoordinator;

    /// <summary>
    /// Initializes configuration loading over explicit Git execution and parsing services.
    /// </summary>
    /// <param name="installation">The resolved Git installation.</param>
    /// <param name="runner">The sole child-process runner.</param>
    /// <param name="environmentFactory">The operation-specific child-environment factory.</param>
    /// <param name="parser">The bounded configuration response parser.</param>
    /// <param name="mutationCoordinator">The repository mutation coordinator, or none for a read-only service.</param>
    internal GitConfigurationService(
        GitInstallation installation,
        IChildProcessRunner runner,
        GitChildEnvironmentFactory environmentFactory,
        GitConfigurationParser parser,
        RepositoryMutationCoordinator? mutationCoordinator = null)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(environmentFactory);
        ArgumentNullException.ThrowIfNull(parser);
        _installation = installation;
        _runner = runner;
        _environmentFactory = environmentFactory;
        _parser = parser;
        _mutationCoordinator = mutationCoordinator;
    }

    /// <summary>
    /// Loads every visible configuration entry without collapsing precedence or duplicate values.
    /// </summary>
    /// <param name="workingDirectory">The canonical directory whose repository configuration is visible.</param>
    /// <param name="cancellationToken">Signals configuration loading cancellation.</param>
    /// <returns>The ordered explicit configuration entries reported by Git.</returns>
    internal async Task<ImmutableArray<GitConfigurationEntry>> LoadAsync(
        CanonicalDirectory workingDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);

        var invocation = new ProcessInvocation(
            _installation.Executable,
            [
                ProcessArgument.Literal("--no-pager"),
                ProcessArgument.Literal("config"),
                ProcessArgument.Literal("--null"),
                ProcessArgument.Literal("--list"),
                ProcessArgument.Literal("--show-origin"),
                ProcessArgument.Literal("--show-scope"),
            ],
            workingDirectory,
            _environmentFactory.CreateConfigurationReadEnvironment(),
            StandardInputSource.Empty(),
            OutputPolicy.Create(64 * 1024 * 1024, 1024 * 1024));
        var result = await _runner.RunAsync(invocation, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            var error = Encoding.UTF8.GetString(result.StandardError.Span).Trim();
            throw new GitCommandException(
                result.ExitCode,
                string.IsNullOrEmpty(error) ? "Git configuration loading failed." : error);
        }

        return _parser.Parse(result.StandardOutput.Span);
    }

    /// <summary>
    /// Loads every visible entry and exposes typed explicit, inherited, empty, invalid, and absent states.
    /// </summary>
    /// <param name="workingDirectory">The canonical directory whose repository configuration is visible.</param>
    /// <param name="cancellationToken">Signals configuration loading cancellation.</param>
    /// <returns>The ordered raw entries and typed registry resolver.</returns>
    internal async Task<GitConfigurationSnapshot> LoadSnapshotAsync(
        CanonicalDirectory workingDirectory,
        CancellationToken cancellationToken)
        => new(await LoadAsync(workingDirectory, cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// Replaces all explicit values for one registered key at an exact writable scope.
    /// </summary>
    /// <param name="workingDirectory">The canonical repository working directory.</param>
    /// <param name="scope">The exact global, local, or worktree scope.</param>
    /// <param name="key">The exact registered concrete key.</param>
    /// <param name="value">The exact validated replacement value.</param>
    /// <param name="cancellationToken">Signals cancellation while waiting or executing Git.</param>
    /// <returns>A task that completes after Git commits the configuration update.</returns>
    internal Task SetAsync(
        CanonicalDirectory workingDirectory,
        GitConfigurationScope scope,
        GitConfigurationKey key,
        GitConfigurationValue value,
        CancellationToken cancellationToken)
        => MutateAsync(
            workingDirectory,
            scope,
            key,
            value,
            "--replace-all",
            requireMultipleValues: false,
            cancellationToken);

    /// <summary>
    /// Adds one explicit value to a registered multivalue key at an exact writable scope.
    /// </summary>
    /// <param name="workingDirectory">The canonical repository working directory.</param>
    /// <param name="scope">The exact global, local, or worktree scope.</param>
    /// <param name="key">The exact registered concrete key.</param>
    /// <param name="value">The exact validated value to append.</param>
    /// <param name="cancellationToken">Signals cancellation while waiting or executing Git.</param>
    /// <returns>A task that completes after Git commits the configuration update.</returns>
    internal Task AddAsync(
        CanonicalDirectory workingDirectory,
        GitConfigurationScope scope,
        GitConfigurationKey key,
        GitConfigurationValue value,
        CancellationToken cancellationToken)
        => MutateAsync(
            workingDirectory,
            scope,
            key,
            value,
            "--add",
            requireMultipleValues: true,
            cancellationToken);

    /// <summary>
    /// Removes every selected-scope occurrence equal to one exact value from a registered multivalue key.
    /// </summary>
    /// <param name="workingDirectory">The canonical repository working directory.</param>
    /// <param name="scope">The exact global, local, or worktree scope.</param>
    /// <param name="key">The exact registered concrete key.</param>
    /// <param name="value">The exact existing value bytes selected for removal.</param>
    /// <param name="cancellationToken">Signals cancellation while waiting or executing Git.</param>
    /// <returns>A task that completes after Git removes all equal selected-scope occurrences, if present.</returns>
    internal async Task RemoveValueAsync(
        CanonicalDirectory workingDirectory,
        GitConfigurationScope scope,
        GitConfigurationKey key,
        GitConfigurationValue value,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        var definition = GetWritableDefinition(key, scope);
        if (!definition.AllowsMultipleValues)
        {
            throw new ArgumentException(
                $"Configuration key '{key.DisplayText}' is not registered as multivalue.",
                nameof(key));
        }

        var coordinator = _mutationCoordinator ?? throw new InvalidOperationException(
            "Configuration writes require the repository mutation coordinator.");
        await using var lease = await coordinator.AcquireAsync(
            RepositoryMutationPurpose.Configuration,
            cancellationToken).ConfigureAwait(false);
        await EnsureWorktreeScopeEnabledAsync(
            workingDirectory,
            scope,
            cancellationToken).ConfigureAwait(false);
        var result = await RunAsync(
            workingDirectory,
            [
                ProcessArgument.Literal(GetScopeOption(scope)),
                ProcessArgument.Literal("--fixed-value"),
                ProcessArgument.Literal("--unset-all"),
                ProcessArgument.Literal("--"),
                ProcessArgument.Native(key),
                ProcessArgument.Native(value),
            ],
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode is not (0 or 5))
        {
            ThrowMutationFailure(result, "Git configuration value removal failed.");
        }
    }

    /// <summary>
    /// Removes only the selected scope's explicit values so normal inheritance becomes visible.
    /// </summary>
    /// <param name="workingDirectory">The canonical repository working directory.</param>
    /// <param name="scope">The exact global, local, or worktree scope.</param>
    /// <param name="key">The exact registered concrete key.</param>
    /// <param name="cancellationToken">Signals cancellation while waiting or executing Git.</param>
    /// <returns>A task that completes after Git removes the explicit value, if present.</returns>
    internal async Task UnsetAsync(
        CanonicalDirectory workingDirectory,
        GitConfigurationScope scope,
        GitConfigurationKey key,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);
        ArgumentNullException.ThrowIfNull(key);
        var definition = GetWritableDefinition(key, scope);
        _ = definition;
        var coordinator = _mutationCoordinator ?? throw new InvalidOperationException(
            "Configuration writes require the repository mutation coordinator.");
        await using var lease = await coordinator.AcquireAsync(
            RepositoryMutationPurpose.Configuration,
            cancellationToken).ConfigureAwait(false);
        await EnsureWorktreeScopeEnabledAsync(
            workingDirectory,
            scope,
            cancellationToken).ConfigureAwait(false);
        var result = await RunAsync(
            workingDirectory,
            [
                ProcessArgument.Literal(GetScopeOption(scope)),
                ProcessArgument.Literal("--unset-all"),
                ProcessArgument.Literal("--"),
                ProcessArgument.Native(key),
            ],
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode is not (0 or 5))
        {
            ThrowMutationFailure(result, "Git configuration reset failed.");
        }
    }

    /// <summary>
    /// Writes every supported property for one validated user-defined tool at one exact scope.
    /// </summary>
    /// <param name="workingDirectory">The canonical repository working directory.</param>
    /// <param name="scope">The exact global, local, or worktree scope.</param>
    /// <param name="configuration">The complete validated configured-tool values.</param>
    /// <param name="cancellationToken">Signals cancellation while waiting or executing Git.</param>
    /// <returns>A task that completes after every property is reconciled.</returns>
    internal async Task SaveConfiguredToolAsync(
        CanonicalDirectory workingDirectory,
        GitConfigurationScope scope,
        ConfiguredToolConfiguration configuration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);
        ArgumentNullException.ThrowIfNull(configuration);
        if (!ConfiguredToolConfigurationValidator.TryValidate(configuration, out var error))
        {
            throw new ArgumentException(error, nameof(configuration));
        }

        var coordinator = _mutationCoordinator ?? throw new InvalidOperationException(
            "Configuration writes require the repository mutation coordinator.");
        await using var lease = await coordinator.AcquireAsync(
            RepositoryMutationPurpose.Configuration,
            cancellationToken).ConfigureAwait(false);
        await EnsureWorktreeScopeEnabledAsync(
            workingDirectory,
            scope,
            cancellationToken).ConfigureAwait(false);

        foreach (var property in ConfiguredToolConfigurationProperties.All)
        {
            var value = GetConfiguredToolProperty(configuration, property);
            await ReconcileConfiguredToolPropertyAsync(
                workingDirectory,
                scope,
                configuration.Name,
                property,
                value,
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Removes every supported explicit property for one user-defined tool at one exact scope.
    /// </summary>
    /// <param name="workingDirectory">The canonical repository working directory.</param>
    /// <param name="scope">The exact global, local, or worktree scope.</param>
    /// <param name="name">The exact configured-tool subsection name.</param>
    /// <param name="cancellationToken">Signals cancellation while waiting or executing Git.</param>
    /// <returns>A task that completes after every explicit property is absent.</returns>
    internal async Task RemoveConfiguredToolAsync(
        CanonicalDirectory workingDirectory,
        GitConfigurationScope scope,
        string name,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);
        ArgumentNullException.ThrowIfNull(name);
        if (!ConfiguredToolConfigurationValidator.TryValidateName(name, out var error))
        {
            throw new ArgumentException(error, nameof(name));
        }

        var coordinator = _mutationCoordinator ?? throw new InvalidOperationException(
            "Configuration writes require the repository mutation coordinator.");
        await using var lease = await coordinator.AcquireAsync(
            RepositoryMutationPurpose.Configuration,
            cancellationToken).ConfigureAwait(false);
        await EnsureWorktreeScopeEnabledAsync(
            workingDirectory,
            scope,
            cancellationToken).ConfigureAwait(false);
        foreach (var property in ConfiguredToolConfigurationProperties.All)
        {
            await UnsetConfiguredToolPropertyAsync(
                workingDirectory,
                scope,
                name,
                property,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task MutateAsync(
        CanonicalDirectory workingDirectory,
        GitConfigurationScope scope,
        GitConfigurationKey key,
        GitConfigurationValue value,
        string operation,
        bool requireMultipleValues,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        var definition = GetWritableDefinition(key, scope);
        if (requireMultipleValues && !definition.AllowsMultipleValues)
        {
            throw new ArgumentException(
                $"Configuration key '{key.DisplayText}' is not registered as multivalue.",
                nameof(key));
        }

        if (!GitConfigurationValueValidator.TryParse(definition, value, out _, out var validationError))
        {
            throw new ArgumentException(validationError, nameof(value));
        }

        var coordinator = _mutationCoordinator ?? throw new InvalidOperationException(
            "Configuration writes require the repository mutation coordinator.");
        await using var lease = await coordinator.AcquireAsync(
            RepositoryMutationPurpose.Configuration,
            cancellationToken).ConfigureAwait(false);
        await EnsureWorktreeScopeEnabledAsync(
            workingDirectory,
            scope,
            cancellationToken).ConfigureAwait(false);
        var result = await RunAsync(
            workingDirectory,
            [
                ProcessArgument.Literal(GetScopeOption(scope)),
                ProcessArgument.Literal(operation),
                ProcessArgument.Literal("--"),
                ProcessArgument.Native(key),
                ProcessArgument.Native(value),
            ],
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            ThrowMutationFailure(result, "Git configuration update failed.");
        }
    }

    private async Task EnsureWorktreeScopeEnabledAsync(
        CanonicalDirectory workingDirectory,
        GitConfigurationScope scope,
        CancellationToken cancellationToken)
    {
        if (scope != GitConfigurationScope.Worktree)
        {
            return;
        }

        var result = await RunAsync(
            workingDirectory,
            [
                ProcessArgument.Literal("--local"),
                ProcessArgument.Literal("--type=bool"),
                ProcessArgument.Literal("--get"),
                ProcessArgument.Literal("extensions.worktreeConfig"),
            ],
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode == 1)
        {
            throw new RepositoryPreconditionException(
                "Worktree-specific configuration is not enabled for this repository. " +
                "Enable extensions.worktreeConfig before saving a worktree-only value.");
        }

        if (result.ExitCode != 0)
        {
            ThrowMutationFailure(result, "Git could not inspect worktree configuration support.");
        }

        var enabled = Encoding.ASCII.GetString(result.StandardOutput.Span).Trim();
        if (!string.Equals(enabled, bool.TrueString, StringComparison.OrdinalIgnoreCase))
        {
            throw new RepositoryPreconditionException(
                "Worktree-specific configuration is disabled for this repository. " +
                "Enable extensions.worktreeConfig before saving a worktree-only value.");
        }
    }

    private async Task ReconcileConfiguredToolPropertyAsync(
        CanonicalDirectory workingDirectory,
        GitConfigurationScope scope,
        string name,
        string property,
        string? value,
        CancellationToken cancellationToken)
    {
        if (value is null)
        {
            await UnsetConfiguredToolPropertyAsync(
                workingDirectory,
                scope,
                name,
                property,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var key = GitConfigurationKey.FromBytes(
            s_strictUtf8.GetBytes($"guitool.{name}.{property}"));
        var configurationValue = GitConfigurationValue.FromBytes(s_strictUtf8.GetBytes(value));
        var definition = GetWritableDefinition(key, scope);
        if (!GitConfigurationValueValidator.TryParse(
            definition,
            configurationValue,
            out _,
            out var validationError))
        {
            throw new ArgumentException(validationError, nameof(value));
        }

        var result = await RunAsync(
            workingDirectory,
            [
                ProcessArgument.Literal(GetScopeOption(scope)),
                ProcessArgument.Literal("--replace-all"),
                ProcessArgument.Literal("--"),
                ProcessArgument.Native(key),
                ProcessArgument.Native(configurationValue),
            ],
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            ThrowMutationFailure(result, "Git configured-tool update failed.");
        }
    }

    private async Task UnsetConfiguredToolPropertyAsync(
        CanonicalDirectory workingDirectory,
        GitConfigurationScope scope,
        string name,
        string property,
        CancellationToken cancellationToken)
    {
        var key = GitConfigurationKey.FromBytes(
            s_strictUtf8.GetBytes($"guitool.{name}.{property}"));
        _ = GetWritableDefinition(key, scope);
        var result = await RunAsync(
            workingDirectory,
            [
                ProcessArgument.Literal(GetScopeOption(scope)),
                ProcessArgument.Literal("--unset-all"),
                ProcessArgument.Literal("--"),
                ProcessArgument.Native(key),
            ],
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode is not (0 or 5))
        {
            ThrowMutationFailure(result, "Git configured-tool removal failed.");
        }
    }

    private static string? GetConfiguredToolProperty(
        ConfiguredToolConfiguration configuration,
        string property)
        => property switch
        {
            "cmd" => configuration.Command,
            "title" => EmptyToNull(configuration.Title),
            "prompt" => EmptyToNull(configuration.Prompt),
            "argprompt" => EmptyToNull(configuration.ArgumentPrompt),
            "revprompt" => EmptyToNull(configuration.RevisionPrompt),
            "noconsole" => configuration.NoConsole ? "true" : "false",
            "needsfile" => configuration.NeedsFile ? "true" : "false",
            "confirm" => configuration.Confirm ? "true" : "false",
            "revunmerged" => configuration.RevisionUnmerged ? "true" : "false",
            "norescan" => configuration.NoRescan ? "true" : "false",
            _ => throw new ArgumentOutOfRangeException(nameof(property)),
        };

    private static string? EmptyToNull(string value)
        => value.Length == 0 ? null : value;

    private static GitConfigurationDefinition GetWritableDefinition(
        GitConfigurationKey key,
        GitConfigurationScope scope)
    {
        var definition = GitConfigurationRegistry.Find(key.DisplayText)
            ?? throw new ArgumentException(
                $"Configuration key '{key.DisplayText}' is not registered.",
                nameof(key));
        if (!definition.CanWrite(scope))
        {
            throw new ArgumentException(
                $"Configuration key '{key.DisplayText}' cannot be written at {scope.ToString().ToLowerInvariant()} scope.",
                nameof(scope));
        }

        return definition;
    }

    private Task<ProcessResult> RunAsync(
        CanonicalDirectory workingDirectory,
        IReadOnlyList<ProcessArgument> arguments,
        CancellationToken cancellationToken)
        => _runner.RunAsync(
            new ProcessInvocation(
                _installation.Executable,
                [
                    ProcessArgument.Literal("--no-pager"),
                    ProcessArgument.Literal("config"),
                    .. arguments,
                ],
                workingDirectory,
                _environmentFactory.CreateRepositoryMutationEnvironment(),
                StandardInputSource.Empty(),
                OutputPolicy.Create(1024 * 1024, 1024 * 1024)),
            cancellationToken);

    private static string GetScopeOption(GitConfigurationScope scope)
        => scope switch
        {
            GitConfigurationScope.Global => "--global",
            GitConfigurationScope.Local => "--local",
            GitConfigurationScope.Worktree => "--worktree",
            _ => throw new ArgumentOutOfRangeException(
                nameof(scope),
                scope,
                "Only global, local, and worktree configuration can be written."),
        };

    private static void ThrowMutationFailure(ProcessResult result, string fallback)
    {
        var error = Encoding.UTF8.GetString(result.StandardError.Span).Trim();
        throw new GitCommandException(
            result.ExitCode,
            string.IsNullOrEmpty(error) ? fallback : error);
    }
}
