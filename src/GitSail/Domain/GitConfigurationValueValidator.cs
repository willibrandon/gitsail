using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace GitSail.Domain;

/// <summary>
/// Converts exact configuration bytes into registry-defined typed values without normalization loss.
/// </summary>
internal static class GitConfigurationValueValidator
{
    private static readonly UTF8Encoding s_strictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private static readonly ImmutableHashSet<string> s_colorNames =
        ImmutableHashSet.Create(
            StringComparer.OrdinalIgnoreCase,
            "normal",
            "default",
            "black",
            "red",
            "green",
            "yellow",
            "blue",
            "magenta",
            "cyan",
            "white");
    private static readonly ImmutableHashSet<string> s_colorAttributes =
        ImmutableHashSet.Create(
            StringComparer.OrdinalIgnoreCase,
            "bold",
            "dim",
            "ul",
            "blink",
            "reverse",
            "italic",
            "strike",
            "nobold",
            "nodim",
            "noul",
            "noblink",
            "noreverse",
            "noitalic",
            "nostrike");

    /// <summary>
    /// Parses an exact configuration value according to one registry definition.
    /// </summary>
    /// <param name="definition">The matching registry definition.</param>
    /// <param name="value">The exact explicit value bytes.</param>
    /// <param name="parsed">The typed interpretation when successful.</param>
    /// <param name="error">The specific validation error when unsuccessful.</param>
    /// <returns><see langword="true"/> when the value is valid for the definition.</returns>
    internal static bool TryParse(
        GitConfigurationDefinition definition,
        GitConfigurationValue value,
        out GitConfigurationParsedValue? parsed,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(value);
        if (definition.ValueKind == GitConfigurationValueKind.NativePath)
        {
            return TryParseNativePath(value, out parsed, out error);
        }

        string text;
        try
        {
            text = s_strictUtf8.GetString(value.GetBytes());
        }
        catch (DecoderFallbackException)
        {
            parsed = null;
            error = "The value is not valid UTF-8.";
            return false;
        }

        return TryParseText(definition, text, out parsed, out error);
    }

    /// <summary>
    /// Parses a managed configuration value before a typed write.
    /// </summary>
    /// <param name="definition">The matching registry definition.</param>
    /// <param name="text">The proposed managed value.</param>
    /// <param name="parsed">The typed interpretation when successful.</param>
    /// <param name="error">The specific validation error when unsuccessful.</param>
    /// <returns><see langword="true"/> when the value is valid for the definition.</returns>
    internal static bool TryParseText(
        GitConfigurationDefinition definition,
        string text,
        out GitConfigurationParsedValue? parsed,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(text);
        if (text.Contains('\0', StringComparison.Ordinal))
        {
            parsed = null;
            error = "Configuration values cannot contain NUL.";
            return false;
        }

        switch (definition.ValueKind)
        {
            case GitConfigurationValueKind.String:
                return Success(text, items: [], out parsed, out error);
            case GitConfigurationValueKind.NativePath:
                return TryParseManagedNativePath(text, out parsed, out error);
            case GitConfigurationValueKind.Boolean:
                return TryParseBoolean(text, out parsed, out error);
            case GitConfigurationValueKind.Integer:
                return TryParseInteger(definition, text, out parsed, out error);
            case GitConfigurationValueKind.Enumeration:
                return TryParseEnumeration(definition, text, out parsed, out error);
            case GitConfigurationValueKind.Color:
                return TryParseColor(text, out parsed, out error);
            case GitConfigurationValueKind.DiffOptions:
                if (GitDiffOptions.TryParse(text, out var options, out error))
                {
                    return Success(text, options, out parsed, out error);
                }

                parsed = null;
                return false;
            case GitConfigurationValueKind.ChordList:
                return TryParseChordList(text, out parsed, out error);
            case GitConfigurationValueKind.Layout:
                return TryParseJsonRecord(text, "layout", out parsed, out error);
            case GitConfigurationValueKind.Capability:
                return TryParseJsonRecord(text, "capability grant", out parsed, out error);
            default:
                throw new ArgumentOutOfRangeException(nameof(definition));
        }
    }

    private static bool TryParseBoolean(
        string text,
        out GitConfigurationParsedValue? parsed,
        out string? error)
    {
        var value = text.ToUpperInvariant() switch
        {
            "TRUE" or "YES" or "ON" or "1" => true,
            "" or "FALSE" or "NO" or "OFF" or "0" => false,
            _ => (bool?)null,
        };
        if (value is null)
        {
            parsed = null;
            error = "Expected a Git boolean: true/false, yes/no, on/off, or 1/0.";
            return false;
        }

        parsed = new GitConfigurationParsedValue(
            value.Value ? bool.TrueString : bool.FalseString,
            value,
            null,
            [],
            null);
        error = null;
        return true;
    }

    private static bool TryParseInteger(
        GitConfigurationDefinition definition,
        string text,
        out GitConfigurationParsedValue? parsed,
        out string? error)
    {
        var numberText = text.AsSpan();
        long multiplier = 1;
        if (!numberText.IsEmpty && char.IsLetter(numberText[^1]))
        {
            multiplier = char.ToUpperInvariant(numberText[^1]) switch
            {
                'K' => 1024L,
                'M' => 1024L * 1024L,
                'G' => 1024L * 1024L * 1024L,
                _ => 0,
            };
            numberText = numberText[..^1];
        }

        if (multiplier == 0 ||
            !long.TryParse(numberText, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number))
        {
            parsed = null;
            error = "Expected a Git integer with an optional k, m, or g binary-size suffix.";
            return false;
        }

        try
        {
            number = checked(number * multiplier);
        }
        catch (OverflowException)
        {
            parsed = null;
            error = "The Git integer is outside the supported 64-bit range.";
            return false;
        }

        if (definition.Minimum is { } minimum && number < minimum ||
            definition.Maximum is { } maximum && number > maximum)
        {
            parsed = null;
            error = $"Expected an integer from {definition.Minimum?.ToString(CultureInfo.InvariantCulture) ?? "-∞"} " +
                $"through {definition.Maximum?.ToString(CultureInfo.InvariantCulture) ?? "+∞"}.";
            return false;
        }

        parsed = new GitConfigurationParsedValue(
            number.ToString(CultureInfo.InvariantCulture),
            null,
            number,
            [],
            null);
        error = null;
        return true;
    }

    private static bool TryParseEnumeration(
        GitConfigurationDefinition definition,
        string text,
        out GitConfigurationParsedValue? parsed,
        out string? error)
    {
        var canonical = definition.AllowedValues.FirstOrDefault(
            value => string.Equals(value, text, StringComparison.OrdinalIgnoreCase));
        if (canonical is null)
        {
            parsed = null;
            error = $"Expected one of: {string.Join(", ", definition.AllowedValues)}.";
            return false;
        }

        return Success(canonical, [], out parsed, out error);
    }

    private static bool TryParseColor(
        string text,
        out GitConfigurationParsedValue? parsed,
        out string? error)
    {
        var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0 || tokens.Length > 4 || tokens.Any(static token => !IsColorToken(token)))
        {
            parsed = null;
            error = "Expected Git color names, RGB values, palette indexes, and supported attributes.";
            return false;
        }

        return Success(text, [.. tokens], out parsed, out error);
    }

    private static bool IsColorToken(string token)
    {
        if (s_colorNames.Contains(token) || s_colorAttributes.Contains(token))
        {
            return true;
        }

        if (token.Length == 7 && token[0] == '#' &&
            token.AsSpan(1).IndexOfAnyExcept("0123456789abcdefABCDEF") < 0)
        {
            return true;
        }

        return byte.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out _);
    }

    private static bool TryParseChordList(
        string text,
        out GitConfigurationParsedValue? parsed,
        out string? error)
    {
        var chords = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (chords.Length == 0 || chords.Any(static chord => !IsChord(chord)))
        {
            parsed = null;
            error = "Expected comma-separated key chords such as Ctrl+R or Shift+F3.";
            return false;
        }

        return Success(text, [.. chords], out parsed, out error);
    }

    private static bool IsChord(string chord)
    {
        var parts = chord.Split('+', StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || parts.Any(string.IsNullOrEmpty))
        {
            return false;
        }

        for (var index = 0; index < parts.Length - 1; index++)
        {
            if (parts[index] is not ("Ctrl" or "Alt" or "Shift"))
            {
                return false;
            }
        }

        return parts[^1].All(static character => char.IsLetterOrDigit(character) || character is '-' or '[' or ']');
    }

    private static bool TryParseJsonRecord(
        string text,
        string description,
        out GitConfigurationParsedValue? parsed,
        out string? error)
    {
        try
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16,
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("version", out var version) ||
                version.ValueKind != JsonValueKind.Number ||
                !version.TryGetInt32(out var versionNumber) ||
                versionNumber != 1)
            {
                parsed = null;
                error = $"The {description} must be a version-1 JSON object.";
                return false;
            }
        }
        catch (JsonException exception)
        {
            parsed = null;
            error = $"The {description} is invalid JSON: {exception.Message}";
            return false;
        }

        return Success(text, [], out parsed, out error);
    }

    private static bool TryParseNativePath(
        GitConfigurationValue value,
        out GitConfigurationParsedValue? parsed,
        out string? error)
    {
        if (value.IsEmpty)
        {
            parsed = new GitConfigurationParsedValue(string.Empty, null, null, [], null);
            error = null;
            return true;
        }

        try
        {
            var path = OperatingSystem.IsWindows()
                ? GitPath.FromWindowsPath(s_strictUtf8.GetString(value.GetBytes()))
                : GitPath.FromUnixBytes(value.GetBytes());
            parsed = new GitConfigurationParsedValue(path.DisplayText, null, null, [], path);
            error = null;
            return true;
        }
        catch (DecoderFallbackException)
        {
            parsed = null;
            error = "The Windows path is not valid UTF-8.";
            return false;
        }
        catch (ArgumentException exception)
        {
            parsed = null;
            error = $"The native path is invalid: {exception.Message}";
            return false;
        }
    }

    private static bool TryParseManagedNativePath(
        string text,
        out GitConfigurationParsedValue? parsed,
        out string? error)
    {
        if (text.Length == 0)
        {
            parsed = new GitConfigurationParsedValue(string.Empty, null, null, [], null);
            error = null;
            return true;
        }

        try
        {
            var path = OperatingSystem.IsWindows()
                ? GitPath.FromWindowsPath(text)
                : GitPath.FromUnixBytes(s_strictUtf8.GetBytes(text));
            parsed = new GitConfigurationParsedValue(path.DisplayText, null, null, [], path);
            error = null;
            return true;
        }
        catch (ArgumentException exception)
        {
            parsed = null;
            error = $"The native path is invalid: {exception.Message}";
            return false;
        }
    }

    private static bool Success(
        string text,
        ImmutableArray<string> items,
        out GitConfigurationParsedValue? parsed,
        out string? error)
    {
        parsed = new GitConfigurationParsedValue(text, null, null, items, null);
        error = null;
        return true;
    }
}
