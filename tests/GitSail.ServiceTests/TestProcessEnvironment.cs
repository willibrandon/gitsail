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

    /// <inheritdoc />
    public bool IsWindows => OperatingSystem.IsWindows();

    /// <inheritdoc />
    public string? GetVariable(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return _variables.TryGetValue(name, out var value) ? value : null;
    }
}
