using GitSail.Git.Execution;

namespace GitSail.ServiceTests;

/// <summary>
/// Supplies isolated process-environment values to execution service tests.
/// </summary>
internal sealed class TestProcessEnvironment : IProcessEnvironment
{
    private readonly IReadOnlyDictionary<string, string?> _variables;

    /// <summary>
    /// Initializes an isolated process environment.
    /// </summary>
    /// <param name="variables">The available variable values.</param>
    internal TestProcessEnvironment(IReadOnlyDictionary<string, string?> variables)
    {
        ArgumentNullException.ThrowIfNull(variables);
        _variables = variables;
    }

    /// <summary>
    /// Creates a Git child-environment factory isolated beneath one test home.
    /// </summary>
    /// <param name="homeDirectory">The test-owned home and configuration directory.</param>
    /// <returns>An operation-specific factory that cannot read developer Git configuration.</returns>
    internal static GitChildEnvironmentFactory CreateGitFactory(string homeDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(homeDirectory);
        return new GitChildEnvironmentFactory(new TestProcessEnvironment(
            new Dictionary<string, string?>
            {
                ["HOME"] = homeDirectory,
                ["USERPROFILE"] = homeDirectory,
                ["XDG_CONFIG_HOME"] = Path.Combine(homeDirectory, "xdg-config"),
                ["GIT_CONFIG_NOSYSTEM"] = "1",
            }));
    }

    /// <inheritdoc />
    public bool IsWindows => OperatingSystem.IsWindows();

    /// <inheritdoc />
    public string? GetVariable(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return _variables.TryGetValue(name, out var value) ? value : null;
    }
}
