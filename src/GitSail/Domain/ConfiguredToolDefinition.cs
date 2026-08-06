namespace GitSail.Domain;

/// <summary>
/// Describes one effective user-defined Git GUI tool and its exact source semantics.
/// </summary>
/// <param name="Name">The exact configured tool subsection name.</param>
/// <param name="Title">The effective user-facing tool title.</param>
/// <param name="ConfigurationKey">The exact concrete command configuration key.</param>
/// <param name="Command">The exact effective shell command, when valid.</param>
/// <param name="SourceScope">The effective command source scope.</param>
/// <param name="SourceOrigin">The exact effective command source origin.</param>
/// <param name="Prompt">The optional confirmation prompt text.</param>
/// <param name="ArgumentPrompt">The optional argument-input label.</param>
/// <param name="RevisionPrompt">The optional revision-input label.</param>
/// <param name="NoConsole">Whether successful execution suppresses the output dialog.</param>
/// <param name="NeedsFile">Whether execution requires one focused changed path.</param>
/// <param name="Confirm">Whether execution requires explicit confirmation.</param>
/// <param name="RevisionUnmerged">Whether revision input is intended for unmerged state.</param>
/// <param name="NoRescan">Whether successful execution skips repository refresh.</param>
/// <param name="UnavailableReason">The exact validation reason preventing execution.</param>
internal sealed record ConfiguredToolDefinition(
    string Name,
    string Title,
    string ConfigurationKey,
    string? Command,
    GitConfigurationScope SourceScope,
    GitConfigurationOrigin SourceOrigin,
    string? Prompt,
    string? ArgumentPrompt,
    string? RevisionPrompt,
    bool NoConsole,
    bool NeedsFile,
    bool Confirm,
    bool RevisionUnmerged,
    bool NoRescan,
    string? UnavailableReason)
{
    /// <summary>
    /// Gets whether the effective tool configuration is safe to present for capability review.
    /// </summary>
    internal bool IsAvailable => UnavailableReason is null && Command is not null;
}
