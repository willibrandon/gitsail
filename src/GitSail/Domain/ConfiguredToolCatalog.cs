using System.Collections.Immutable;
using System.Text;

namespace GitSail.Domain;

/// <summary>
/// Builds a bounded ordered catalog from effective <c>guitool.*</c> configuration values.
/// </summary>
internal sealed class ConfiguredToolCatalog
{
    private const int MaximumTools = 256;
    private const int MaximumNameCharacters = 512;
    private const int MaximumTitleCharacters = 512;
    private const int MaximumPromptCharacters = 4096;
    private const int MaximumCommandCharacters = 256 * 1024;
    private static readonly UTF8Encoding s_utf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private ConfiguredToolCatalog(
        ImmutableArray<ConfiguredToolDefinition> tools,
        string? warning)
    {
        Tools = tools;
        Warning = warning;
    }

    /// <summary>
    /// Gets every retained effective tool in ordinal name order.
    /// </summary>
    internal ImmutableArray<ConfiguredToolDefinition> Tools { get; }

    /// <summary>
    /// Gets the bounded-catalog warning when excess configured tools were omitted.
    /// </summary>
    internal string? Warning { get; }

    /// <summary>
    /// Creates the effective catalog without executing or resolving any configured command.
    /// </summary>
    /// <param name="configuration">The complete ordered Git configuration snapshot.</param>
    /// <returns>The bounded effective configured-tool catalog.</returns>
    internal static ConfiguredToolCatalog Create(GitConfigurationSnapshot configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var commandEntries = configuration.Entries
            .Where(static entry => IsCommandKey(entry.Key.GetBytes()))
            .GroupBy(static entry => Convert.ToHexString(entry.Key.GetBytes()), StringComparer.Ordinal)
            .Select(static group => (Key: group.Key, Entry: group.Last()))
            .OrderBy(static item => item.Key, StringComparer.Ordinal)
            .Select(static item => item.Entry)
            .ToArray();
        var retained = commandEntries.Take(MaximumTools).Select(entry =>
            CreateDefinition(configuration, entry)).ToImmutableArray();
        var warning = commandEntries.Length > MaximumTools
            ? $"Only the first {MaximumTools} of {commandEntries.Length} configured tools are shown."
            : null;
        return new ConfiguredToolCatalog(retained, warning);
    }

    private static ConfiguredToolDefinition CreateDefinition(
        GitConfigurationSnapshot configuration,
        GitConfigurationEntry commandEntry)
    {
        string configurationKey;
        try
        {
            configurationKey = s_utf8.GetString(commandEntry.Key.GetBytes());
        }
        catch (DecoderFallbackException)
        {
            var displayKey = commandEntry.Key.DisplayText;
            return Unavailable(
                displayKey,
                displayKey,
                commandEntry,
                "The configured tool name is not valid UTF-8.");
        }

        var name = configurationKey[8..^4];
        if (name.Length == 0 || name.Length > MaximumNameCharacters)
        {
            return Unavailable(
                name.Length == 0 ? configurationKey : name,
                configurationKey,
                commandEntry,
                "The configured tool name is empty or exceeds the supported length.");
        }

        var commandResolution = configuration.Resolve(
            configurationKey,
            GitConfigurationScope.Local);
        var effectiveEntry = commandResolution.EffectiveEntry ?? commandEntry;
        var command = commandResolution.EffectiveParsedValue?.Text;
        var unavailableReason = commandResolution.EffectiveValidationError;
        if (command is null || command.Length == 0)
        {
            unavailableReason ??= "The configured tool command is empty.";
        }
        else if (command.Length > MaximumCommandCharacters)
        {
            unavailableReason = "The configured tool command exceeds the 256 Ki-character limit.";
            command = null;
        }

        var title = GetText(configuration, name, "title") ?? name;
        if (title.Length == 0)
        {
            title = name;
        }

        if (title.Length > MaximumTitleCharacters)
        {
            title = $"{title[..(MaximumTitleCharacters - 3)]}...";
        }

        var prompt = LimitPrompt(GetText(configuration, name, "prompt"));
        var argumentPrompt = LimitPrompt(GetText(configuration, name, "argprompt"));
        var revisionPrompt = LimitPrompt(GetText(configuration, name, "revprompt"));
        return new ConfiguredToolDefinition(
            name,
            title,
            configurationKey,
            command,
            effectiveEntry.Scope,
            effectiveEntry.Origin,
            prompt,
            argumentPrompt,
            revisionPrompt,
            GetBoolean(configuration, name, "noconsole"),
            GetBoolean(configuration, name, "needsfile"),
            GetBoolean(configuration, name, "confirm"),
            GetBoolean(configuration, name, "revunmerged"),
            GetBoolean(configuration, name, "norescan"),
            unavailableReason);
    }

    private static ConfiguredToolDefinition Unavailable(
        string name,
        string configurationKey,
        GitConfigurationEntry commandEntry,
        string reason)
        => new(
            name,
            name,
            configurationKey,
            null,
            commandEntry.Scope,
            commandEntry.Origin,
            null,
            null,
            null,
            NoConsole: false,
            NeedsFile: false,
            Confirm: false,
            RevisionUnmerged: false,
            NoRescan: false,
            reason);

    private static string? GetText(
        GitConfigurationSnapshot configuration,
        string name,
        string property)
        => configuration.Resolve(
            $"guitool.{name}.{property}",
            GitConfigurationScope.Local).EffectiveParsedValue?.Text;

    private static bool GetBoolean(
        GitConfigurationSnapshot configuration,
        string name,
        string property)
        => configuration.Resolve(
            $"guitool.{name}.{property}",
            GitConfigurationScope.Local).EffectiveParsedValue?.BooleanValue == true;

    private static string? LimitPrompt(string? value)
        => value is null || value.Length <= MaximumPromptCharacters
            ? value
            : $"{value[..(MaximumPromptCharacters - 3)]}...";

    private static bool IsCommandKey(ReadOnlySpan<byte> key)
        => key.Length > 12 &&
            key[..8].SequenceEqual("guitool."u8) &&
            key[^4..].SequenceEqual(".cmd"u8);
}
