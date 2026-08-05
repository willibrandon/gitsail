namespace GitSail.Domain;

/// <summary>
/// Describes one registered key at a selected scope without losing its effective source or validity.
/// </summary>
/// <param name="Key">The exact concrete configuration key.</param>
/// <param name="Definition">The matching registry definition.</param>
/// <param name="SelectedScope">The scope being inspected or edited.</param>
/// <param name="State">How the value is supplied at the selected scope.</param>
/// <param name="ExplicitEntry">The last explicit entry at the selected scope, when present.</param>
/// <param name="EffectiveEntry">The effective explicit entry across all scopes, when present.</param>
/// <param name="ExplicitParsedValue">The typed selected-scope value, when present and valid.</param>
/// <param name="ExplicitValidationError">The selected-scope validation failure, when applicable.</param>
/// <param name="EffectiveParsedValue">The typed effective value or registered default, when valid.</param>
/// <param name="EffectiveValidationError">The effective-value validation failure, when applicable.</param>
internal sealed record ResolvedGitConfigurationValue(
    string Key,
    GitConfigurationDefinition Definition,
    GitConfigurationScope SelectedScope,
    GitConfigurationResolutionState State,
    GitConfigurationEntry? ExplicitEntry,
    GitConfigurationEntry? EffectiveEntry,
    GitConfigurationParsedValue? ExplicitParsedValue,
    string? ExplicitValidationError,
    GitConfigurationParsedValue? EffectiveParsedValue,
    string? EffectiveValidationError);
