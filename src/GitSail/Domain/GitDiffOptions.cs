using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace GitSail.Domain;

/// <summary>
/// Parses and validates the non-executable diff options accepted from Git configuration.
/// </summary>
internal static class GitDiffOptions
{
    private const int MaximumOptionCount = 32;
    private const int MaximumValueLength = 4096;
    private static readonly ImmutableHashSet<string> s_exactOptions =
        ImmutableHashSet.Create(
            StringComparer.Ordinal,
            "-w",
            "-b",
            "--ignore-all-space",
            "--ignore-space-change",
            "--ignore-space-at-eol",
            "--ignore-cr-at-eol",
            "--ignore-blank-lines",
            "--minimal",
            "--patience",
            "--histogram",
            "--stat",
            "--numstat",
            "--shortstat",
            "--compact-summary");
    private static readonly ImmutableArray<string> s_numericPrefixes =
    [
        "--unified=",
        "--inter-hunk-context=",
        "--stat-width=",
        "--stat-name-width=",
        "--stat-graph-width=",
        "--stat-count=",
    ];

    /// <summary>
    /// Parses one Git GUI-compatible option list and rejects output, path, color, and execution switches.
    /// </summary>
    /// <param name="text">The configured option-list text.</param>
    /// <param name="options">The parsed allowlisted options when successful.</param>
    /// <param name="error">The specific validation failure when unsuccessful.</param>
    /// <returns><see langword="true"/> when every option is structurally safe and supported.</returns>
    internal static bool TryParse(
        string text,
        out ImmutableArray<string> options,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > MaximumValueLength)
        {
            options = [];
            error = $"Diff options exceed the {MaximumValueLength.ToString(CultureInfo.InvariantCulture)}-character limit.";
            return false;
        }

        if (!TryTokenize(text, out options, out error))
        {
            return false;
        }

        return TryValidateItems(options, out error);
    }

    /// <summary>
    /// Revalidates already-tokenized diff options before they cross the child-process boundary.
    /// </summary>
    /// <param name="options">The ordered option tokens to validate.</param>
    /// <param name="error">The specific validation failure when unsuccessful.</param>
    /// <returns><see langword="true"/> when every token remains bounded and allowlisted.</returns>
    internal static bool TryValidateItems(
        ImmutableArray<string> options,
        out string? error)
    {
        if (options.IsDefault)
        {
            options = [];
        }

        if (options.Length > MaximumOptionCount)
        {
            error = $"Diff options contain more than {MaximumOptionCount.ToString(CultureInfo.InvariantCulture)} entries.";
            return false;
        }

        var totalLength = 0;
        foreach (var option in options)
        {
            if (option is null)
            {
                error = "Diff options cannot contain a null token.";
                return false;
            }

            if (option.Length > MaximumValueLength ||
                totalLength + option.Length + 1 > MaximumValueLength + 1)
            {
                error = $"Diff options exceed the {MaximumValueLength.ToString(CultureInfo.InvariantCulture)}-character limit.";
                return false;
            }

            totalLength += option.Length + 1;

            if (!IsAllowed(option))
            {
                error = $"Diff option '{option}' is not in the context, whitespace, algorithm, or stat allowlist.";
                return false;
            }
        }

        error = null;
        return true;
    }

    private static bool IsAllowed(string option)
    {
        if (s_exactOptions.Contains(option))
        {
            return true;
        }

        if (option.Length > 2 && option.StartsWith("-U", StringComparison.Ordinal))
        {
            return IsNonnegativeInteger(option.AsSpan(2));
        }

        foreach (var prefix in s_numericPrefixes)
        {
            if (option.StartsWith(prefix, StringComparison.Ordinal) &&
                IsNonnegativeInteger(option.AsSpan(prefix.Length)))
            {
                return true;
            }
        }

        if (option.StartsWith("--diff-algorithm=", StringComparison.Ordinal))
        {
            return option.AsSpan("--diff-algorithm=".Length) is
                "myers" or "minimal" or "patience" or "histogram";
        }

        if (option.StartsWith("--dirstat", StringComparison.Ordinal))
        {
            return option.Length == "--dirstat".Length ||
                option.StartsWith("--dirstat=", StringComparison.Ordinal);
        }

        if (option.StartsWith("--ws-error-highlight=", StringComparison.Ordinal))
        {
            var value = option.AsSpan("--ws-error-highlight=".Length);
            return value is "none" or "old" or "new" or "context" or "all" or "default";
        }

        return option.StartsWith("--anchored=", StringComparison.Ordinal) &&
            option.Length > "--anchored=".Length &&
            !option.AsSpan("--anchored=".Length).ContainsAny('\r', '\n', '\0');
    }

    private static bool IsNonnegativeInteger(ReadOnlySpan<char> text)
        => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out _);

    private static bool TryTokenize(
        string text,
        out ImmutableArray<string> options,
        out string? error)
    {
        var result = ImmutableArray.CreateBuilder<string>();
        var token = new StringBuilder();
        var quote = false;
        var braceDepth = 0;
        var escaped = false;
        var tokenStarted = false;

        foreach (var character in text)
        {
            if (escaped)
            {
                token.Append(character);
                tokenStarted = true;
                escaped = false;
                continue;
            }

            if (character == '\\')
            {
                escaped = true;
                tokenStarted = true;
                continue;
            }

            if (braceDepth > 0)
            {
                if (character == '{')
                {
                    braceDepth++;
                    if (braceDepth > 1)
                    {
                        token.Append(character);
                    }
                }
                else if (character == '}')
                {
                    braceDepth--;
                    if (braceDepth > 0)
                    {
                        token.Append(character);
                    }
                }
                else
                {
                    token.Append(character);
                }

                tokenStarted = true;
                continue;
            }

            if (quote)
            {
                if (character == '"')
                {
                    quote = false;
                }
                else
                {
                    token.Append(character);
                }

                tokenStarted = true;
                continue;
            }

            if (character == '"')
            {
                quote = true;
                tokenStarted = true;
            }
            else if (character == '{')
            {
                braceDepth = 1;
                tokenStarted = true;
            }
            else if (char.IsWhiteSpace(character))
            {
                CompleteToken(result, token, ref tokenStarted);
            }
            else
            {
                token.Append(character);
                tokenStarted = true;
            }
        }

        if (escaped || quote || braceDepth != 0)
        {
            options = [];
            error = "Diff options contain an incomplete escape, quote, or brace group.";
            return false;
        }

        CompleteToken(result, token, ref tokenStarted);
        options = result.ToImmutable();
        error = null;
        return true;
    }

    private static void CompleteToken(
        ImmutableArray<string>.Builder result,
        StringBuilder token,
        ref bool tokenStarted)
    {
        if (!tokenStarted)
        {
            return;
        }

        result.Add(token.ToString());
        token.Clear();
        tokenStarted = false;
    }
}
