using System.Collections.Immutable;

namespace GitSail.Domain;

/// <summary>
/// Defines the complete supported Git GUI tool property set in stable write order.
/// </summary>
internal static class ConfiguredToolConfigurationProperties
{
    /// <summary>
    /// Gets every supported property name with the required command first.
    /// </summary>
    internal static ImmutableArray<string> All { get; } =
    [
        "cmd",
        "title",
        "prompt",
        "argprompt",
        "revprompt",
        "noconsole",
        "needsfile",
        "confirm",
        "revunmerged",
        "norescan",
    ];
}
