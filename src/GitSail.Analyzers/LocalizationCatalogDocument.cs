using System.Runtime.Serialization;

namespace GitSail.Analyzers;

/// <summary>
/// Represents one JSON localization catalog consumed only during compilation.
/// </summary>
[DataContract]
internal sealed class LocalizationCatalogDocument
{
    /// <summary>
    /// Gets or sets the BCP 47 locale name declared by the catalog.
    /// </summary>
    [DataMember(Name = "locale", IsRequired = true)]
    public string? Locale { get; set; }

    /// <summary>
    /// Gets or sets the catalog license declaration.
    /// </summary>
    [DataMember(Name = "license", IsRequired = true)]
    public string? License { get; set; }

    /// <summary>
    /// Gets or sets whether translation review is complete.
    /// </summary>
    [DataMember(Name = "reviewed", IsRequired = true)]
    public bool Reviewed { get; set; }

    /// <summary>
    /// Gets or sets the messages in the catalog.
    /// </summary>
    [DataMember(Name = "messages", IsRequired = true)]
    public LocalizationMessageDocument[]? Messages { get; set; }
}
