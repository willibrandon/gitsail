namespace GitSail.Domain;

/// <summary>
/// Represents one explicit Git configuration value with its precedence and origin.
/// </summary>
/// <param name="Scope">The precedence scope reported by Git.</param>
/// <param name="Origin">The exact origin reported by Git.</param>
/// <param name="Key">The exact canonical Git configuration key.</param>
/// <param name="Value">The exact explicit configuration value.</param>
internal sealed record GitConfigurationEntry(
    GitConfigurationScope Scope,
    GitConfigurationOrigin Origin,
    GitConfigurationKey Key,
    GitConfigurationValue Value);
