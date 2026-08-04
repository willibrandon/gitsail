namespace GitSail.Domain;

/// <summary>
/// Contains one immutable conflict stage and its exact object bytes when the stage is blob-backed.
/// </summary>
internal sealed class ConflictStageContent
{
    private readonly byte[]? _content;

    /// <summary>
    /// Initializes one stage result with independently owned optional blob content.
    /// </summary>
    /// <param name="stage">The exact mode and object ID from the unmerged index.</param>
    /// <param name="content">The exact blob bytes, or <see langword="null"/> for a gitlink.</param>
    internal ConflictStageContent(ConflictStage stage, ReadOnlyMemory<byte>? content)
    {
        ArgumentNullException.ThrowIfNull(stage);
        Stage = stage;
        _content = content?.ToArray();
    }

    /// <summary>
    /// Gets the exact mode and object ID from the unmerged index.
    /// </summary>
    internal ConflictStage Stage { get; }

    /// <summary>
    /// Gets the exact independently owned blob bytes, or null for a gitlink stage.
    /// </summary>
    internal ReadOnlyMemory<byte>? Content => _content;
}
