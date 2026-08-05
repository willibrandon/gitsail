using System.Runtime.Serialization;

namespace GitSail.Analyzers;

/// <summary>
/// Describes how one localized message chooses a pattern variant.
/// </summary>
[DataContract]
internal sealed class LocalizationSelectorDocument
{
    /// <summary>
    /// Gets or sets the selector kind, either <c>plural</c> or <c>select</c>.
    /// </summary>
    [DataMember(Name = "kind", IsRequired = true)]
    public string? Kind { get; set; }

    /// <summary>
    /// Gets or sets the named argument used as the selector.
    /// </summary>
    [DataMember(Name = "argument", IsRequired = true)]
    public string? Argument { get; set; }
}
