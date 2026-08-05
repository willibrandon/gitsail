using GitSail.Git.Execution;

namespace GitSail.CompatibilityTests;

/// <summary>
/// Supplies deterministic isolated environment values to compatibility tests.
/// </summary>
internal sealed class CompatibilityProcessEnvironment : IProcessEnvironment
{
    private readonly Dictionary<string, string?> _variables;

    /// <summary>
    /// Initializes a deterministic environment rooted beneath one test-owned directory.
    /// </summary>
    /// <param name="homeDirectory">The isolated home, configuration, and temporary directory.</param>
    internal CompatibilityProcessEnvironment(string homeDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(homeDirectory);
        _variables = new Dictionary<string, string?>
        {
            ["HOME"] = homeDirectory,
            ["USERPROFILE"] = homeDirectory,
            ["XDG_CONFIG_HOME"] = Path.Combine(homeDirectory, "xdg-config"),
            ["APPDATA"] = Path.Combine(homeDirectory, "appdata"),
            ["LOCALAPPDATA"] = Path.Combine(homeDirectory, "localappdata"),
            ["GIT_CONFIG_NOSYSTEM"] = "1",
            ["GIT_AUTHOR_NAME"] = "GitSail Compatibility",
            ["GIT_AUTHOR_EMAIL"] = "compatibility@example.invalid",
            ["GIT_AUTHOR_DATE"] = "2000-01-01T00:00:00Z",
            ["GIT_COMMITTER_NAME"] = "GitSail Compatibility",
            ["GIT_COMMITTER_EMAIL"] = "compatibility@example.invalid",
            ["GIT_COMMITTER_DATE"] = "2000-01-01T00:00:00Z",
            ["GIT_EDITOR"] = OperatingSystem.IsWindows() ? "cmd /c exit 0" : ":",
            ["PATH"] = Environment.GetEnvironmentVariable("PATH"),
            ["SystemRoot"] = Environment.GetEnvironmentVariable("SystemRoot"),
            ["WINDIR"] = Environment.GetEnvironmentVariable("WINDIR"),
            ["TEMP"] = homeDirectory,
            ["TMP"] = homeDirectory,
            ["TMPDIR"] = homeDirectory,
        };
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
