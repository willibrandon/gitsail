using GitSail.Domain;

namespace GitSail.Ui;

/// <summary>
/// Identifies one exact configured key or one dynamic-key template shown in the options browser.
/// </summary>
/// <param name="Key">The exact concrete key or registered pattern.</param>
/// <param name="Definition">The matching typed registry definition.</param>
/// <param name="IsTemplate">Whether the row requires a concrete key before it can be saved.</param>
internal sealed record GitConfigurationOptionItem(
    string Key,
    GitConfigurationDefinition Definition,
    bool IsTemplate)
{
    /// <summary>
    /// Gets the stable list identity that distinguishes a template from a configured concrete key.
    /// </summary>
    internal string Id => IsTemplate ? $"template:{Key}" : $"key:{Key}";

    /// <summary>
    /// Returns the concise key label rendered by the default typed-list row.
    /// </summary>
    /// <returns>The registered pattern with an add marker, or the exact concrete key.</returns>
    public override string ToString()
        => IsTemplate ? $"{Key}  (add concrete key)" : Key;
}
