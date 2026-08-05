using GitSail.Domain;

namespace GitSail.Ui;

/// <summary>
/// Presents one ordered selected-scope value without replacing its exact retained bytes.
/// </summary>
/// <param name="Index">The zero-based position in Git's selected-scope value order.</param>
/// <param name="Entry">The exact raw configuration entry.</param>
/// <param name="DisplayValue">The terminal-safe and credential-redacted display value.</param>
internal sealed record GitConfigurationExplicitValueItem(
    int Index,
    GitConfigurationEntry Entry,
    string DisplayValue)
{
    /// <summary>
    /// Gets the stable list identity for this ordered snapshot entry.
    /// </summary>
    internal string Id => $"value:{Index}";

    /// <summary>
    /// Returns the concise one-based value label rendered by the default typed-list row.
    /// </summary>
    /// <returns>The ordered position and safe display value.</returns>
    public override string ToString()
        => $"{Index + 1}: {DisplayValue}";
}
