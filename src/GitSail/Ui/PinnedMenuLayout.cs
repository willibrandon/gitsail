namespace GitSail.Ui;

/// <summary>
/// Identifies one persisted non-modal menu window and its last settled geometry.
/// </summary>
internal readonly record struct PinnedMenuLayout(
    string Id,
    int X,
    int Y,
    int Width,
    int Height);
