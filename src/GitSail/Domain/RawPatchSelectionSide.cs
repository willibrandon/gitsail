namespace GitSail.Domain;

/// <summary>
/// Identifies the unchanged patch side retained while unselected changes are removed.
/// </summary>
internal enum RawPatchSelectionSide
{
    /// <summary>
    /// Retains old-side content so the generated patch applies in its forward direction.
    /// </summary>
    PreserveOldSide,

    /// <summary>
    /// Retains new-side content so the generated patch applies in its reverse direction.
    /// </summary>
    PreserveNewSide,
}
