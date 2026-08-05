namespace GitSail.Localization;

/// <summary>
/// Identifies one Unicode plural category used by generated message variants.
/// </summary>
internal enum PluralCategory
{
    /// <summary>
    /// Represents the language-specific zero category.
    /// </summary>
    Zero,

    /// <summary>
    /// Represents the language-specific singular category.
    /// </summary>
    One,

    /// <summary>
    /// Represents the language-specific dual category.
    /// </summary>
    Two,

    /// <summary>
    /// Represents the language-specific few category.
    /// </summary>
    Few,

    /// <summary>
    /// Represents the language-specific many category.
    /// </summary>
    Many,

    /// <summary>
    /// Represents the required plural fallback category.
    /// </summary>
    Other,
}
