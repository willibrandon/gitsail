using System.Collections.Immutable;

namespace GitSail.Domain;

/// <summary>
/// Retains the canonical typed interpretation of one registered configuration value.
/// </summary>
/// <param name="Text">The decoded text or canonical enumeration spelling.</param>
/// <param name="BooleanValue">The parsed boolean value, when applicable.</param>
/// <param name="IntegerValue">The parsed integer value, when applicable.</param>
/// <param name="Items">The parsed option or chord items, when applicable.</param>
/// <param name="NativePath">The exact native path, when applicable.</param>
internal sealed record GitConfigurationParsedValue(
    string Text,
    bool? BooleanValue,
    long? IntegerValue,
    ImmutableArray<string> Items,
    GitPath? NativePath);
