using System.Runtime.Serialization;

namespace GitSail.Analyzers;

/// <summary>
/// Represents one semantically identified localized message.
/// </summary>
[DataContract]
internal sealed class LocalizationMessageDocument
{
    /// <summary>
    /// Gets or sets the stable semantic message identifier.
    /// </summary>
    [DataMember(Name = "id", IsRequired = true)]
    public string? Id { get; set; }

    /// <summary>
    /// Gets or sets the non-selecting message pattern.
    /// </summary>
    [DataMember(Name = "text", EmitDefaultValue = false)]
    public string? Text { get; set; }

    /// <summary>
    /// Gets or sets the translator-facing explanation.
    /// </summary>
    [DataMember(Name = "description", IsRequired = true)]
    public string? Description { get; set; }

    /// <summary>
    /// Gets or sets the named argument types used by the patterns.
    /// </summary>
    [DataMember(Name = "arguments", EmitDefaultValue = false)]
    public Dictionary<string, string>? Arguments { get; set; }

    /// <summary>
    /// Gets or sets the optional plural or value selector.
    /// </summary>
    [DataMember(Name = "selector", EmitDefaultValue = false)]
    public LocalizationSelectorDocument? Selector { get; set; }

    /// <summary>
    /// Gets or sets the selector variants keyed by plural category or exact value.
    /// </summary>
    [DataMember(Name = "variants", EmitDefaultValue = false)]
    public Dictionary<string, string>? Variants { get; set; }

    /// <summary>
    /// Gets or sets the annotated accelerator character.
    /// </summary>
    [DataMember(Name = "accelerator", EmitDefaultValue = false)]
    public string? Accelerator { get; set; }

    /// <summary>
    /// Gets or sets the menu scope in which the accelerator must be unique.
    /// </summary>
    [DataMember(Name = "menu", EmitDefaultValue = false)]
    public string? Menu { get; set; }

    /// <summary>
    /// Gets or sets the terminal-width handling policy.
    /// </summary>
    [DataMember(Name = "widthPolicy", IsRequired = true)]
    public string? WidthPolicy { get; set; }

    /// <summary>
    /// Gets or sets the hard maximum width when the policy is <c>hard</c>.
    /// </summary>
    [DataMember(Name = "maximumColumns", EmitDefaultValue = false)]
    public int? MaximumColumns { get; set; }
}
