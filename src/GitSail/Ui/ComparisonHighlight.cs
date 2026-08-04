namespace GitSail.Ui;

/// <summary>
/// Describes one exact intraline range in a comparison presentation document.
/// </summary>
/// <param name="Line">The one-based presentation line.</param>
/// <param name="StartColumn">The one-based inclusive start column.</param>
/// <param name="EndColumn">The one-based exclusive end column.</param>
/// <param name="IsAddition">Whether the range belongs to added text.</param>
internal sealed record ComparisonHighlight(
    int Line,
    int StartColumn,
    int EndColumn,
    bool IsAddition);
