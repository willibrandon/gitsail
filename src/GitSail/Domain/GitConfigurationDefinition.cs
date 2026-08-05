using System.Collections.Immutable;

namespace GitSail.Domain;

/// <summary>
/// Defines the type, default, scope, and presentation contract for one configuration key pattern.
/// </summary>
/// <param name="KeyPattern">The exact key or one-star dynamic key pattern.</param>
/// <param name="ValueKind">The parser and validation contract.</param>
/// <param name="DefaultValue">The application default, or <see langword="null"/> when absence is meaningful.</param>
/// <param name="WritableScopes">The user-writable scopes accepted by the options service.</param>
/// <param name="AllowedValues">The canonical values accepted by an enumeration.</param>
/// <param name="Minimum">The inclusive integer lower bound, when one applies.</param>
/// <param name="Maximum">The inclusive integer upper bound, when one applies.</param>
/// <param name="AllowsMultipleValues">Whether the key may retain more than one explicit value.</param>
/// <param name="IsTerminalApplicable">Whether the setting changes terminal behavior.</param>
/// <param name="ExecutionKind">How the value can select executable behavior.</param>
/// <param name="MayContainSecret">Whether diagnostics must redact credentials or tokens in the value.</param>
/// <param name="Description">The concise user-facing purpose.</param>
internal sealed record GitConfigurationDefinition(
    string KeyPattern,
    GitConfigurationValueKind ValueKind,
    string? DefaultValue,
    GitConfigurationScopeMask WritableScopes,
    ImmutableArray<string> AllowedValues,
    long? Minimum,
    long? Maximum,
    bool AllowsMultipleValues,
    bool IsTerminalApplicable,
    GitConfigurationExecutionKind ExecutionKind,
    bool MayContainSecret,
    string Description)
{
    /// <summary>
    /// Gets whether this definition represents a dynamic configuration-key family.
    /// </summary>
    internal bool IsPattern => KeyPattern.Contains('*', StringComparison.Ordinal);

    /// <summary>
    /// Determines whether one canonical key belongs to this exact or dynamic definition.
    /// </summary>
    /// <param name="key">The canonical key to inspect.</param>
    /// <returns><see langword="true"/> when the key matches this definition.</returns>
    internal bool Matches(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var wildcard = KeyPattern.IndexOf('*', StringComparison.Ordinal);
        if (wildcard < 0)
        {
            return string.Equals(KeyPattern, key, StringComparison.OrdinalIgnoreCase);
        }

        var prefix = KeyPattern.AsSpan(0, wildcard);
        var suffix = KeyPattern.AsSpan(wildcard + 1);
        return key.Length > prefix.Length + suffix.Length &&
            key.AsSpan(0, prefix.Length).Equals(prefix, StringComparison.OrdinalIgnoreCase) &&
            key.AsSpan(key.Length - suffix.Length).Equals(suffix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Gets whether the selected scope is writable for this definition.
    /// </summary>
    /// <param name="scope">The selected Git configuration scope.</param>
    /// <returns><see langword="true"/> when writes to the scope are registered.</returns>
    internal bool CanWrite(GitConfigurationScope scope)
        => (WritableScopes & scope.ToMask()) != GitConfigurationScopeMask.None;
}
