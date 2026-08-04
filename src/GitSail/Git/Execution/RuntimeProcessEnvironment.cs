namespace GitSail.Git.Execution;

/// <summary>
/// Reads explicitly requested values from the current process environment.
/// </summary>
internal sealed class RuntimeProcessEnvironment : IProcessEnvironment
{
    /// <inheritdoc />
    public bool IsWindows => OperatingSystem.IsWindows();

    /// <inheritdoc />
    public string? GetVariable(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return Environment.GetEnvironmentVariable(name);
    }
}
