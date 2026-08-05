using System.Collections.Immutable;

namespace GitSail.Analyzers;

/// <summary>
/// Parses safe named-argument markers from localized message patterns.
/// </summary>
internal static class LocalizationPatternParser
{
    /// <summary>
    /// Parses a message pattern containing <c>{ $name }</c> markers.
    /// </summary>
    /// <param name="pattern">The localized message pattern.</param>
    /// <param name="parts">The parsed literal and argument parts.</param>
    /// <param name="error">The validation error when parsing fails.</param>
    /// <returns><see langword="true"/> when the complete pattern is valid.</returns>
    internal static bool TryParse(
        string pattern,
        out ImmutableArray<LocalizationPatternPart> parts,
        out string? error)
    {
        var builder = ImmutableArray.CreateBuilder<LocalizationPatternPart>();
        var literalStart = 0;
        var index = 0;
        while (index < pattern.Length)
        {
            if (pattern[index] == '}')
            {
                parts = [];
                error = "contains an unmatched closing brace";
                return false;
            }

            if (pattern[index] != '{')
            {
                index++;
                continue;
            }

            if (index > literalStart)
            {
                builder.Add(new LocalizationPatternPart(
                    pattern.Substring(literalStart, index - literalStart),
                    isArgument: false));
            }

            var markerStart = index++;
            SkipSpaces(pattern, ref index);
            if (index >= pattern.Length || pattern[index] != '$')
            {
                parts = [];
                error = $"contains an invalid marker at character {markerStart}";
                return false;
            }

            index++;
            var nameStart = index;
            if (index >= pattern.Length || !IsIdentifierStart(pattern[index]))
            {
                parts = [];
                error = $"contains an invalid argument name at character {nameStart}";
                return false;
            }

            index++;
            while (index < pattern.Length && IsIdentifierPart(pattern[index]))
            {
                index++;
            }

            var name = pattern.Substring(nameStart, index - nameStart);
            SkipSpaces(pattern, ref index);
            if (index >= pattern.Length || pattern[index] != '}')
            {
                parts = [];
                error = $"does not close argument '{name}'";
                return false;
            }

            index++;
            builder.Add(new LocalizationPatternPart(name, isArgument: true));
            literalStart = index;
        }

        if (literalStart < pattern.Length)
        {
            builder.Add(new LocalizationPatternPart(pattern.Substring(literalStart), isArgument: false));
        }

        parts = builder.ToImmutable();
        error = null;
        return true;
    }

    private static void SkipSpaces(string pattern, ref int index)
    {
        while (index < pattern.Length && pattern[index] == ' ')
        {
            index++;
        }
    }

    private static bool IsIdentifierStart(char value)
        => value is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or '_';

    private static bool IsIdentifierPart(char value)
        => IsIdentifierStart(value) || value is >= '0' and <= '9';
}
