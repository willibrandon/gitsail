using Hex1b.Theming;

namespace GitSail.Ui;

/// <summary>
/// Defines the theme-controlled underline color for possible commit-message misspellings.
/// </summary>
internal static class GitSailSpellingTheme
{
    /// <summary>
    /// Gets the underline color for possible misspellings in the commit editor.
    /// </summary>
    internal static readonly Hex1bThemeElement<Hex1bColor> UnderlineColor = new(
        $"{nameof(GitSailSpellingTheme)}.{nameof(UnderlineColor)}",
        () => Hex1bColor.FromRgb(255, 190, 60));
}
