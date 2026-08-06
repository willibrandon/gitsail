namespace GitSail.Domain;

/// <summary>
/// Contains every editable value for one user-defined Git GUI tool.
/// </summary>
/// <param name="Name">The exact tool subsection name.</param>
/// <param name="Command">The required opaque shell command.</param>
/// <param name="Title">The optional user-facing title.</param>
/// <param name="Prompt">The optional confirmation prompt.</param>
/// <param name="ArgumentPrompt">The optional argument-input prompt.</param>
/// <param name="RevisionPrompt">The optional revision-input prompt.</param>
/// <param name="NoConsole">Whether successful execution suppresses its output window.</param>
/// <param name="NeedsFile">Whether the tool requires one focused changed path.</param>
/// <param name="Confirm">Whether the tool always asks for confirmation before execution.</param>
/// <param name="RevisionUnmerged">Whether revision input is intended for unmerged state.</param>
/// <param name="NoRescan">Whether successful execution skips repository refresh.</param>
internal sealed record ConfiguredToolConfiguration(
    string Name,
    string Command,
    string Title,
    string Prompt,
    string ArgumentPrompt,
    string RevisionPrompt,
    bool NoConsole,
    bool NeedsFile,
    bool Confirm,
    bool RevisionUnmerged,
    bool NoRescan)
{
    /// <summary>
    /// Creates editable values from one effective configured tool.
    /// </summary>
    /// <param name="tool">The effective configured tool to edit.</param>
    /// <returns>A complete editable configuration snapshot.</returns>
    internal static ConfiguredToolConfiguration FromDefinition(ConfiguredToolDefinition tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        return new ConfiguredToolConfiguration(
            tool.Name,
            tool.Command ?? string.Empty,
            tool.Title == tool.Name ? string.Empty : tool.Title,
            tool.Prompt ?? string.Empty,
            tool.ArgumentPrompt ?? string.Empty,
            tool.RevisionPrompt ?? string.Empty,
            tool.NoConsole,
            tool.NeedsFile,
            tool.Confirm,
            tool.RevisionUnmerged,
            tool.NoRescan);
    }
}
