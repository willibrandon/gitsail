using System.Collections.Immutable;
using System.Text;

namespace GitSail.Domain;

/// <summary>
/// Resolves raw ordered Git configuration entries through the complete typed registry.
/// </summary>
internal sealed class GitConfigurationSnapshot
{
    private static readonly UTF8Encoding s_utf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    /// <summary>
    /// Initializes a configuration snapshot without collapsing duplicate values or origins.
    /// </summary>
    /// <param name="entries">The ordered explicit entries reported by Git.</param>
    internal GitConfigurationSnapshot(ImmutableArray<GitConfigurationEntry> entries)
    {
        Entries = entries.IsDefault ? [] : entries;
    }

    /// <summary>
    /// Gets every ordered explicit entry, including duplicates and command overrides.
    /// </summary>
    internal ImmutableArray<GitConfigurationEntry> Entries { get; }

    /// <summary>
    /// Resolves one concrete registered key for the selected editing scope.
    /// </summary>
    /// <param name="key">The exact concrete configuration key.</param>
    /// <param name="selectedScope">The scope being inspected or edited.</param>
    /// <returns>The explicit, inherited, absent, empty, and validity state.</returns>
    internal ResolvedGitConfigurationValue Resolve(string key, GitConfigurationScope selectedScope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var definition = GitConfigurationRegistry.Find(key)
            ?? throw new ArgumentException($"Configuration key '{key}' is not registered.", nameof(key));
        var matching = Entries
            .Where(entry => string.Equals(entry.Key.DisplayText, key, StringComparison.Ordinal))
            .ToImmutableArray();
        var explicitEntry = matching.LastOrDefault(entry => entry.Scope == selectedScope);
        var effectiveEntry = matching.LastOrDefault();
        var inherited = explicitEntry is null && effectiveEntry is not null;
        GitConfigurationParsedValue? explicitParsed = null;
        string? explicitError = null;
        var explicitValid = explicitEntry is null || GitConfigurationValueValidator.TryParse(
            definition,
            explicitEntry.Value,
            out explicitParsed,
            out explicitError);
        GitConfigurationParsedValue? effectiveParsed;
        string? effectiveError;
        var effectiveValid = effectiveEntry is null
            ? TryParseDefault(definition, out effectiveParsed, out effectiveError)
            : GitConfigurationValueValidator.TryParse(
                definition,
                effectiveEntry.Value,
                out effectiveParsed,
                out effectiveError);
        var state = explicitEntry is null && effectiveEntry is null
            ? GitConfigurationResolutionState.Absent
            : inherited
                ? effectiveValid
                    ? effectiveEntry!.Value.IsEmpty
                        ? GitConfigurationResolutionState.InheritedEmpty
                        : GitConfigurationResolutionState.Inherited
                    : GitConfigurationResolutionState.InheritedInvalid
                : explicitValid
                    ? explicitEntry!.Value.IsEmpty
                        ? GitConfigurationResolutionState.ExplicitEmpty
                        : GitConfigurationResolutionState.Explicit
                    : GitConfigurationResolutionState.ExplicitInvalid;
        return new ResolvedGitConfigurationValue(
            key,
            definition,
            selectedScope,
            state,
            explicitEntry,
            effectiveEntry,
            explicitParsed,
            explicitError,
            effectiveParsed,
            effectiveError);
    }

    /// <summary>
    /// Gets all explicit values for one registered multivalue key and scope in Git order.
    /// </summary>
    /// <param name="key">The exact concrete configuration key.</param>
    /// <param name="scope">The exact scope to inspect.</param>
    /// <returns>The uncollapsed explicit entries in their reported order.</returns>
    internal ImmutableArray<GitConfigurationEntry> GetExplicitValues(
        string key,
        GitConfigurationScope scope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var definition = GitConfigurationRegistry.Find(key)
            ?? throw new ArgumentException($"Configuration key '{key}' is not registered.", nameof(key));
        if (!definition.AllowsMultipleValues)
        {
            throw new ArgumentException($"Configuration key '{key}' is not registered as multivalue.", nameof(key));
        }

        return
        [
            .. Entries.Where(entry => entry.Scope == scope &&
                string.Equals(entry.Key.DisplayText, key, StringComparison.Ordinal)),
        ];
    }

    private static bool TryParseDefault(
        GitConfigurationDefinition definition,
        out GitConfigurationParsedValue? parsed,
        out string? error)
    {
        if (definition.DefaultValue is null)
        {
            parsed = null;
            error = null;
            return true;
        }

        var value = GitConfigurationValue.FromBytes(s_utf8.GetBytes(definition.DefaultValue));
        return GitConfigurationValueValidator.TryParse(definition, value, out parsed, out error);
    }
}
