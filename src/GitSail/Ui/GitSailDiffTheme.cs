using Hex1b.Theming;

namespace GitSail.Ui;

/// <summary>
/// Defines the theme-controlled colors used by every Git patch presentation.
/// </summary>
internal static class GitSailDiffTheme
{
    /// <summary>
    /// Gets the foreground color for unchanged context lines.
    /// </summary>
    internal static readonly Hex1bThemeElement<Hex1bColor> ContextForegroundColor = new(
        $"{nameof(GitSailDiffTheme)}.{nameof(ContextForegroundColor)}",
        () => Hex1bColor.Default);

    /// <summary>
    /// Gets the background color for unchanged context lines.
    /// </summary>
    internal static readonly Hex1bThemeElement<Hex1bColor> ContextBackgroundColor = new(
        $"{nameof(GitSailDiffTheme)}.{nameof(ContextBackgroundColor)}",
        () => Hex1bColor.Default);

    /// <summary>
    /// Gets the foreground color for diff command headers.
    /// </summary>
    internal static readonly Hex1bThemeElement<Hex1bColor> HeaderForegroundColor = new(
        $"{nameof(GitSailDiffTheme)}.{nameof(HeaderForegroundColor)}",
        () => Hex1bColor.FromRgb(255, 200, 60));

    /// <summary>
    /// Gets the background color for diff command headers.
    /// </summary>
    internal static readonly Hex1bThemeElement<Hex1bColor> HeaderBackgroundColor = new(
        $"{nameof(GitSailDiffTheme)}.{nameof(HeaderBackgroundColor)}",
        () => Hex1bColor.Default);

    /// <summary>
    /// Gets the foreground color for index and file metadata.
    /// </summary>
    internal static readonly Hex1bThemeElement<Hex1bColor> MetadataForegroundColor = new(
        $"{nameof(GitSailDiffTheme)}.{nameof(MetadataForegroundColor)}",
        () => Hex1bColor.FromRgb(140, 140, 140));

    /// <summary>
    /// Gets the background color for index and file metadata.
    /// </summary>
    internal static readonly Hex1bThemeElement<Hex1bColor> MetadataBackgroundColor = new(
        $"{nameof(GitSailDiffTheme)}.{nameof(MetadataBackgroundColor)}",
        () => Hex1bColor.Default);

    /// <summary>
    /// Gets the foreground color for removed-file labels.
    /// </summary>
    internal static readonly Hex1bThemeElement<Hex1bColor> OldFileForegroundColor = new(
        $"{nameof(GitSailDiffTheme)}.{nameof(OldFileForegroundColor)}",
        () => Hex1bColor.FromRgb(220, 100, 100));

    /// <summary>
    /// Gets the background color for removed-file labels.
    /// </summary>
    internal static readonly Hex1bThemeElement<Hex1bColor> OldFileBackgroundColor = new(
        $"{nameof(GitSailDiffTheme)}.{nameof(OldFileBackgroundColor)}",
        () => Hex1bColor.Default);

    /// <summary>
    /// Gets the foreground color for added-file labels.
    /// </summary>
    internal static readonly Hex1bThemeElement<Hex1bColor> NewFileForegroundColor = new(
        $"{nameof(GitSailDiffTheme)}.{nameof(NewFileForegroundColor)}",
        () => Hex1bColor.FromRgb(100, 220, 100));

    /// <summary>
    /// Gets the background color for added-file labels.
    /// </summary>
    internal static readonly Hex1bThemeElement<Hex1bColor> NewFileBackgroundColor = new(
        $"{nameof(GitSailDiffTheme)}.{nameof(NewFileBackgroundColor)}",
        () => Hex1bColor.Default);

    /// <summary>
    /// Gets the foreground color for hunk headers.
    /// </summary>
    internal static readonly Hex1bThemeElement<Hex1bColor> HunkForegroundColor = new(
        $"{nameof(GitSailDiffTheme)}.{nameof(HunkForegroundColor)}",
        () => Hex1bColor.FromRgb(80, 200, 220));

    /// <summary>
    /// Gets the background color for hunk headers.
    /// </summary>
    internal static readonly Hex1bThemeElement<Hex1bColor> HunkBackgroundColor = new(
        $"{nameof(GitSailDiffTheme)}.{nameof(HunkBackgroundColor)}",
        () => Hex1bColor.Default);

    /// <summary>
    /// Gets the foreground color for function text inside hunk headers.
    /// </summary>
    internal static readonly Hex1bThemeElement<Hex1bColor> FunctionForegroundColor = new(
        $"{nameof(GitSailDiffTheme)}.{nameof(FunctionForegroundColor)}",
        () => Hex1bColor.Default);

    /// <summary>
    /// Gets the background color for function text inside hunk headers.
    /// </summary>
    internal static readonly Hex1bThemeElement<Hex1bColor> FunctionBackgroundColor = new(
        $"{nameof(GitSailDiffTheme)}.{nameof(FunctionBackgroundColor)}",
        () => Hex1bColor.Default);

    /// <summary>
    /// Gets the foreground color for complete added lines.
    /// </summary>
    internal static readonly Hex1bThemeElement<Hex1bColor> AdditionForegroundColor = new(
        $"{nameof(GitSailDiffTheme)}.{nameof(AdditionForegroundColor)}",
        () => Hex1bColor.FromRgb(80, 220, 80));

    /// <summary>
    /// Gets the background color for complete added lines.
    /// </summary>
    internal static readonly Hex1bThemeElement<Hex1bColor> AdditionBackgroundColor = new(
        $"{nameof(GitSailDiffTheme)}.{nameof(AdditionBackgroundColor)}",
        () => Hex1bColor.FromRgb(20, 40, 20));

    /// <summary>
    /// Gets the foreground color for complete removed lines.
    /// </summary>
    internal static readonly Hex1bThemeElement<Hex1bColor> DeletionForegroundColor = new(
        $"{nameof(GitSailDiffTheme)}.{nameof(DeletionForegroundColor)}",
        () => Hex1bColor.FromRgb(220, 80, 80));

    /// <summary>
    /// Gets the background color for complete removed lines.
    /// </summary>
    internal static readonly Hex1bThemeElement<Hex1bColor> DeletionBackgroundColor = new(
        $"{nameof(GitSailDiffTheme)}.{nameof(DeletionBackgroundColor)}",
        () => Hex1bColor.FromRgb(40, 20, 20));

    /// <summary>
    /// Gets the background color for intraline added ranges.
    /// </summary>
    internal static readonly Hex1bThemeElement<Hex1bColor> AddedRangeBackgroundColor = new(
        $"{nameof(GitSailDiffTheme)}.{nameof(AddedRangeBackgroundColor)}",
        () => Hex1bColor.FromRgb(35, 85, 35));

    /// <summary>
    /// Gets the background color for intraline removed ranges.
    /// </summary>
    internal static readonly Hex1bThemeElement<Hex1bColor> RemovedRangeBackgroundColor = new(
        $"{nameof(GitSailDiffTheme)}.{nameof(RemovedRangeBackgroundColor)}",
        () => Hex1bColor.FromRgb(85, 35, 35));

    /// <summary>
    /// Gets the foreground color for whitespace-error ranges.
    /// </summary>
    internal static readonly Hex1bThemeElement<Hex1bColor> WhitespaceForegroundColor = new(
        $"{nameof(GitSailDiffTheme)}.{nameof(WhitespaceForegroundColor)}",
        () => Hex1bColor.FromRgb(255, 255, 255));

    /// <summary>
    /// Gets the background color for whitespace-error ranges.
    /// </summary>
    internal static readonly Hex1bThemeElement<Hex1bColor> WhitespaceBackgroundColor = new(
        $"{nameof(GitSailDiffTheme)}.{nameof(WhitespaceBackgroundColor)}",
        () => Hex1bColor.FromRgb(180, 0, 0));
}
