namespace GitSail.Git.Execution;

/// <summary>
/// Builds the shell command Git requires for invoking the current sequence-editor helper.
/// </summary>
internal static class SequenceEditorCommandBuilder
{
    /// <summary>
    /// Builds a safely quoted helper command for a native executable or framework-dependent host.
    /// </summary>
    /// <param name="processPath">The absolute executable path for the current process.</param>
    /// <param name="commandLineArguments">The values reported for the current managed process.</param>
    /// <returns>The command assigned to <c>GIT_SEQUENCE_EDITOR</c>.</returns>
    internal static string Build(string processPath, IReadOnlyList<string> commandLineArguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processPath);
        ArgumentNullException.ThrowIfNull(commandLineArguments);
        if (!Path.IsPathFullyQualified(processPath))
        {
            throw new ArgumentException("The current process path must be absolute.", nameof(processPath));
        }

        var values = new List<string> { processPath };
        if (IsDotNetHost(processPath) &&
            commandLineArguments.Count > 0 &&
            string.Equals(Path.GetExtension(commandLineArguments[0]), ".dll", StringComparison.OrdinalIgnoreCase))
        {
            values.Add(Path.GetFullPath(commandLineArguments[0]));
        }

        values.Add("sequence-editor");
        return string.Join(' ', values.Select(QuoteForGitShell));
    }

    private static bool IsDotNetHost(string processPath)
        => string.Equals(
            Path.GetFileNameWithoutExtension(processPath),
            "dotnet",
            StringComparison.OrdinalIgnoreCase);

    private static string QuoteForGitShell(string value)
        => $"'{value.Replace("'", "'\\''", StringComparison.Ordinal)}'";
}
