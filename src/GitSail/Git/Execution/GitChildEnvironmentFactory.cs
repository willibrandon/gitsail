using System.Globalization;

namespace GitSail.Git.Execution;

/// <summary>
/// Builds operation-specific Git child environments from classified startup values.
/// </summary>
internal sealed class GitChildEnvironmentFactory
{
    private const int MaximumCommandConfigurationEntries = 256;
    private readonly IProcessEnvironment _environment;

    /// <summary>
    /// Initializes the factory over an explicit startup-environment source.
    /// </summary>
    /// <param name="environment">The classified startup-environment source.</param>
    internal GitChildEnvironmentFactory(IProcessEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        _environment = environment;
    }

    /// <summary>
    /// Creates the complete environment for a machine-readable read-only configuration query.
    /// </summary>
    /// <returns>An isolated environment that preserves Git configuration provenance.</returns>
    internal ChildEnvironment CreateConfigurationReadEnvironment()
    {
        var variables = CreateConfigurationVariables();
        CopyIfPresent(variables, "GIT_DIR");
        CopyIfPresent(variables, "GIT_WORK_TREE");
        CopyIfPresent(variables, "GIT_COMMON_DIR");
        CopyIfPresent(variables, "GIT_CEILING_DIRECTORIES");
        CopyIfPresent(variables, "GIT_DISCOVERY_ACROSS_FILESYSTEM");
        ApplyMachineReadableDefaults(variables, readOnly: true);
        return ChildEnvironment.Create(variables);
    }

    /// <summary>
    /// Creates the complete environment for initial Git repository discovery.
    /// </summary>
    /// <returns>An isolated environment that honors classified startup repository overrides.</returns>
    internal ChildEnvironment CreateRepositoryDiscoveryEnvironment()
        => CreateConfigurationReadEnvironment();

    /// <summary>
    /// Creates the complete environment for a machine-readable repository read.
    /// </summary>
    /// <returns>An isolated environment with user configuration and no startup repository override.</returns>
    internal ChildEnvironment CreateRepositoryReadEnvironment()
    {
        var variables = CreateConfigurationVariables();
        ApplyMachineReadableDefaults(variables, readOnly: true);
        return ChildEnvironment.Create(variables);
    }

    /// <summary>
    /// Creates the complete environment for a repository mutation.
    /// </summary>
    /// <returns>An isolated environment with user configuration and mutation-safe defaults.</returns>
    internal ChildEnvironment CreateRepositoryMutationEnvironment()
    {
        var variables = CreateConfigurationVariables();
        ApplyMachineReadableDefaults(variables, readOnly: false);
        return ChildEnvironment.Create(variables);
    }

    /// <summary>
    /// Creates the complete environment for a Git-owned commit and its hooks or signer.
    /// </summary>
    /// <returns>An isolated environment with classified identity, tool, locale, and temp values.</returns>
    internal ChildEnvironment CreateCommitEnvironment()
    {
        var variables = CreateConfigurationVariables();
        CopyIfPresent(variables, "PATH");
        CopyIfPresent(variables, "TMPDIR");
        CopyIfPresent(variables, "TEMP");
        CopyIfPresent(variables, "TMP");
        CopyIfPresent(variables, "SHELL");
        CopyIfPresent(variables, "COMSPEC");
        CopyIfPresent(variables, "LANG");
        CopyIfPresent(variables, "LC_ALL");
        CopyIfPresent(variables, "LC_MESSAGES");
        CopyIfPresent(variables, "TERM");
        CopyIfPresent(variables, "GIT_AUTHOR_NAME");
        CopyIfPresent(variables, "GIT_AUTHOR_EMAIL");
        CopyIfPresent(variables, "GIT_AUTHOR_DATE");
        CopyIfPresent(variables, "GIT_COMMITTER_NAME");
        CopyIfPresent(variables, "GIT_COMMITTER_EMAIL");
        CopyIfPresent(variables, "GIT_COMMITTER_DATE");
        CopyIfPresent(variables, "SSH_AUTH_SOCK");
        variables.TryAdd("LANG", "C");
        variables["GIT_PAGER"] = "cat";
        return ChildEnvironment.Create(variables);
    }

    private void CopyCommandConfiguration(Dictionary<string, string> variables)
    {
        var countText = _environment.GetVariable("GIT_CONFIG_COUNT");
        if (countText is null)
        {
            return;
        }

        if (!int.TryParse(
                countText,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var count) ||
            count < 0 ||
            count > MaximumCommandConfigurationEntries)
        {
            throw new InvalidDataException(
                $"GIT_CONFIG_COUNT must be between 0 and {MaximumCommandConfigurationEntries}.");
        }

        variables["GIT_CONFIG_COUNT"] = countText;
        for (var index = 0; index < count; index++)
        {
            CopyIfPresent(variables, $"GIT_CONFIG_KEY_{index.ToString(CultureInfo.InvariantCulture)}");
            CopyIfPresent(variables, $"GIT_CONFIG_VALUE_{index.ToString(CultureInfo.InvariantCulture)}");
        }
    }

    private void CopyIfPresent(Dictionary<string, string> variables, string name)
    {
        var value = _environment.GetVariable(name);
        if (value is not null)
        {
            variables[name] = value;
        }
    }

    private Dictionary<string, string> CreateConfigurationVariables()
    {
        var variables = new Dictionary<string, string>(
            _environment.IsWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        CopyIfPresent(variables, "HOME");
        CopyIfPresent(variables, "USERPROFILE");
        CopyIfPresent(variables, "XDG_CONFIG_HOME");
        CopyIfPresent(variables, "APPDATA");
        CopyIfPresent(variables, "LOCALAPPDATA");
        if (_environment.IsWindows)
        {
            CopyIfPresent(variables, "SystemRoot");
            CopyIfPresent(variables, "WINDIR");
        }

        CopyIfPresent(variables, "GIT_CONFIG_NOSYSTEM");
        CopyIfPresent(variables, "GIT_CONFIG_SYSTEM");
        CopyIfPresent(variables, "GIT_CONFIG_GLOBAL");
        CopyCommandConfiguration(variables);
        return variables;
    }

    private static void ApplyMachineReadableDefaults(
        Dictionary<string, string> variables,
        bool readOnly)
    {
        variables["LANG"] = "C";
        variables["LC_ALL"] = "C";
        variables["GIT_PAGER"] = "cat";
        if (readOnly)
        {
            variables["GIT_OPTIONAL_LOCKS"] = "0";
        }
    }
}
