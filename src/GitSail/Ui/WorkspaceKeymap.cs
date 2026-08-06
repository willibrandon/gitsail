using GitSail.Domain;
using Hex1b.Input;

namespace GitSail.Ui;

/// <summary>
/// Resolves and applies collision-free configured workspace bindings.
/// </summary>
internal static class WorkspaceKeymap
{
    private const string ConfigurationPrefix = "gitsail.keymap.";

    /// <summary>
    /// Applies every valid global workspace override as one atomic keymap.
    /// </summary>
    /// <param name="bindings">The registered baseline workspace bindings.</param>
    /// <param name="configuration">The effective Git configuration snapshot.</param>
    /// <param name="error">Receives the reason the configured keymap was not applied.</param>
    /// <returns><see langword="true"/> when the baseline or complete configured keymap is valid.</returns>
    internal static bool TryApply(
        InputBindingsBuilder bindings,
        GitConfigurationSnapshot configuration,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(configuration);
        var actionIds = bindings.GetAllActionIds().Where(WorkspaceActionIds.IsKnown);
        var overrides = new Dictionary<ActionId, IReadOnlyList<WorkspaceKeyChord>>();
        foreach (var actionId in actionIds)
        {
            var key = ConfigurationPrefix + actionId.Value;
            var resolution = configuration.Resolve(key, GitConfigurationScope.Global);
            if (resolution.EffectiveEntry is null)
            {
                continue;
            }

            if (resolution.EffectiveParsedValue is null)
            {
                error = $"{key} is invalid: {resolution.EffectiveValidationError}";
                return false;
            }

            var chords = new List<WorkspaceKeyChord>();
            foreach (var item in resolution.EffectiveParsedValue.Items)
            {
                if (!WorkspaceKeyChord.TryParse(item, out var chord))
                {
                    error = $"{key} names unsupported baseline chord '{item}'.";
                    return false;
                }

                chords.Add(chord);
            }

            overrides.Add(actionId, chords);
        }

        if (!TryValidate(bindings, overrides, out error))
        {
            return false;
        }

        foreach (var (actionId, chords) in overrides)
        {
            bindings.Remove(actionId);
            foreach (var chord in chords)
            {
                chord.AddTrigger(bindings, actionId);
            }
        }

        return true;
    }

    /// <summary>
    /// Gets the configured display binding for an action when one is present and valid.
    /// </summary>
    /// <param name="configuration">The effective Git configuration snapshot.</param>
    /// <param name="actionId">The stable action identity.</param>
    /// <param name="defaultBinding">The baseline display binding.</param>
    /// <returns>The configured chord list, or the supplied baseline binding.</returns>
    internal static string GetDisplayBinding(
        GitConfigurationSnapshot configuration,
        ActionId actionId,
        string defaultBinding)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(defaultBinding);
        var resolution = configuration.Resolve(
            ConfigurationPrefix + actionId.Value,
            GitConfigurationScope.Global);
        return resolution.EffectiveEntry is not null && resolution.EffectiveParsedValue is { } parsed
            ? string.Join(" / ", parsed.Items)
            : defaultBinding;
    }

    /// <summary>
    /// Validates one edited keymap value against the currently active workspace context.
    /// </summary>
    /// <param name="bindings">The currently active workspace bindings.</param>
    /// <param name="configurationKey">The concrete <c>gitsail.keymap.*</c> key.</param>
    /// <param name="value">The candidate comma-separated chord list.</param>
    /// <param name="error">Receives the unsupported-action or collision explanation.</param>
    /// <returns><see langword="true"/> when the candidate can replace the active action binding.</returns>
    internal static bool TryValidateCandidate(
        InputBindingsBuilder bindings,
        string configurationKey,
        string value,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (!configurationKey.StartsWith(ConfigurationPrefix, StringComparison.Ordinal))
        {
            error = $"'{configurationKey}' is not a workspace keymap setting.";
            return false;
        }

        var actionId = new ActionId(configurationKey[ConfigurationPrefix.Length..]);
        if (!WorkspaceActionIds.IsKnown(actionId) ||
            !bindings.GetAllActionIds().Contains(actionId))
        {
            error = $"Workspace action '{actionId.Value}' is not registered in this context.";
            return false;
        }

        var chords = new List<WorkspaceKeyChord>();
        foreach (var item in value.Split(
            ',',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!WorkspaceKeyChord.TryParse(item, out var chord))
            {
                error = $"'{item}' is not a supported baseline terminal chord.";
                return false;
            }

            chords.Add(chord);
        }

        return TryValidate(
            bindings,
            new Dictionary<ActionId, IReadOnlyList<WorkspaceKeyChord>>
            {
                [actionId] = chords,
            },
            out error);
    }

    private static bool TryValidate(
        InputBindingsBuilder bindings,
        Dictionary<ActionId, IReadOnlyList<WorkspaceKeyChord>> overrides,
        out string? error)
    {
        var owners = new Dictionary<string, ActionId>(StringComparer.Ordinal);
        foreach (var binding in bindings.Bindings)
        {
            if (binding.ActionId is not { } actionId || overrides.ContainsKey(actionId))
            {
                continue;
            }

            if (binding.Steps.Count != 1)
            {
                error = $"Workspace action '{actionId.Value}' uses an unsupported multi-step baseline chord.";
                return false;
            }

            var step = binding.Steps[0];
            if (!AddOwner(
                new WorkspaceKeyChord(step.Key, step.Modifiers),
                actionId,
                owners,
                out error))
            {
                return false;
            }
        }

        foreach (var (actionId, chords) in overrides)
        {
            foreach (var chord in chords)
            {
                if (!AddOwner(chord, actionId, owners, out error))
                {
                    return false;
                }
            }
        }

        error = null;
        return true;
    }

    private static bool AddOwner(
        WorkspaceKeyChord chord,
        ActionId actionId,
        Dictionary<string, ActionId> owners,
        out string? error)
    {
        var identity = chord.GetBaselineIdentity();
        if (owners.TryGetValue(identity, out var existing) && existing != actionId)
        {
            error = $"Configured workspace keymap collision: '{existing.Value}' and " +
                $"'{actionId.Value}' produce the same baseline terminal input.";
            return false;
        }

        owners[identity] = actionId;
        error = null;
        return true;
    }
}
