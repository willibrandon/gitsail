namespace GitSail.Git.Execution;

/// <summary>
/// Provides the process environment values used at an execution boundary.
/// </summary>
internal interface IProcessEnvironment
{
    /// <summary>
    /// Gets whether the current operating system is Windows.
    /// </summary>
    bool IsWindows { get; }

    /// <summary>
    /// Gets one environment variable without exposing the complete environment block.
    /// </summary>
    /// <param name="name">The variable name.</param>
    /// <returns>The variable value, or <see langword="null"/> when it is absent.</returns>
    string? GetVariable(string name);
}
