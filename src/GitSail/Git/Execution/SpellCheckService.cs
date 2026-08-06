using GitSail.Domain;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace GitSail.Git.Execution;

/// <summary>
/// Checks commit-message text through a trusted bounded GNU Aspell pipe invocation.
/// </summary>
internal sealed partial class SpellCheckService
{
    private const int MaximumInputCharacters = 256 * 1024;
    private const int MaximumIssues = 4096;
    private const int MaximumSuggestionsPerIssue = 16;
    private const int MaximumStandardOutputBytes = 4 * 1024 * 1024;
    private const int MaximumStandardErrorBytes = 64 * 1024;
    private static readonly UTF8Encoding s_strictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private readonly ResolvedExecutable _executable;
    private readonly IChildProcessRunner _runner;
    private readonly ChildEnvironment _environment;

    /// <summary>
    /// Initializes the checker over an already resolved executable and explicit process boundary.
    /// </summary>
    /// <param name="executable">The trusted GNU Aspell executable.</param>
    /// <param name="runner">The sole shell-free child-process runner.</param>
    /// <param name="environment">The complete allowlisted child environment.</param>
    internal SpellCheckService(
        ResolvedExecutable executable,
        IChildProcessRunner runner,
        ChildEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(executable);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(environment);
        if (executable.Kind != ProgramKind.Aspell)
        {
            throw new ArgumentException("The spell checker requires a resolved GNU Aspell executable.", nameof(executable));
        }

        _executable = executable;
        _runner = runner;
        _environment = environment;
    }

    /// <summary>
    /// Resolves the optional checker without searching the current directory.
    /// </summary>
    /// <param name="resolver">The trusted executable resolver.</param>
    /// <param name="runner">The sole shell-free child-process runner.</param>
    /// <param name="processEnvironment">The allowlisted startup environment source.</param>
    /// <returns>A ready checker using the resolved executable fingerprint.</returns>
    /// <exception cref="ExecutableResolutionException">GNU Aspell is not available on an absolute path entry.</exception>
    internal static SpellCheckService Create(
        ExecutableResolver resolver,
        IChildProcessRunner runner,
        IProcessEnvironment processEnvironment)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(processEnvironment);
        return new SpellCheckService(
            resolver.Resolve(ProgramKind.Aspell),
            runner,
            CreateEnvironment(processEnvironment));
    }

    /// <summary>
    /// Checks one exact editor version and returns ordered document ranges and suggestions.
    /// </summary>
    /// <param name="workingDirectory">The canonical repository directory used for the optional process.</param>
    /// <param name="message">The complete commit-message text.</param>
    /// <param name="documentVersion">The exact editor version represented by <paramref name="message"/>.</param>
    /// <param name="dictionary">The configured dictionary name, or an empty value for the checker default.</param>
    /// <param name="cancellationToken">Signals child-tree termination and result cancellation.</param>
    /// <returns>The validated spelling result for the supplied document version.</returns>
    /// <exception cref="SpellCheckException">The input, child result, version, or pipe response is invalid.</exception>
    internal async Task<SpellCheckResult> CheckAsync(
        CanonicalDirectory workingDirectory,
        string message,
        long documentVersion,
        string dictionary,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(dictionary);
        if (message.Length > MaximumInputCharacters)
        {
            throw new SpellCheckException(
                $"Spelling is unavailable because the commit message exceeds {MaximumInputCharacters.ToString(CultureInfo.InvariantCulture)} characters.");
        }

        if (message.Contains('\0', StringComparison.Ordinal))
        {
            throw new SpellCheckException("Spelling is unavailable because the commit message contains NUL.");
        }

        ValidateDictionary(dictionary);
        var lines = CreateInputLines(message);
        var input = CreateInput(lines);
        var arguments = ImmutableArray.CreateBuilder<ProcessArgument>(4);
        arguments.Add(ProcessArgument.Literal("--encoding=utf-8"));
        arguments.Add(ProcessArgument.Literal("--mode=none"));
        if (dictionary.Length > 0)
        {
            arguments.Add(ProcessArgument.Literal($"--master={dictionary}"));
        }

        arguments.Add(ProcessArgument.Literal("pipe"));
        var invocation = new ProcessInvocation(
            _executable,
            arguments.ToImmutable(),
            workingDirectory,
            _environment,
            StandardInputSource.FromBytes(input),
            OutputPolicy.Create(MaximumStandardOutputBytes, MaximumStandardErrorBytes));
        ProcessResult result;
        try
        {
            result = await _runner.RunAsync(invocation, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            throw new SpellCheckException("GNU Aspell could not be started safely.", exception);
        }

        if (result.ExitCode != 0)
        {
            var error = DecodeError(result.StandardError.Span);
            throw new SpellCheckException(
                error.Length == 0
                    ? $"GNU Aspell exited with status {result.ExitCode.ToString(CultureInfo.InvariantCulture)}."
                    : $"GNU Aspell failed: {error}");
        }

        try
        {
            var output = s_strictUtf8.GetString(result.StandardOutput.Span);
            return ParseResult(documentVersion, dictionary, message, lines, output);
        }
        catch (DecoderFallbackException exception)
        {
            throw new SpellCheckException("GNU Aspell returned output that is not valid UTF-8.", exception);
        }
    }

    private static ChildEnvironment CreateEnvironment(IProcessEnvironment environment)
    {
        var names = environment.IsWindows
            ? new[]
            {
                "PATH", "HOME", "USERPROFILE", "APPDATA", "LOCALAPPDATA", "TEMP", "TMP",
                "SystemRoot", "WINDIR", "LANG", "LC_ALL", "LC_CTYPE", "ASPELL_CONF",
            }
            : new[]
            {
                "PATH", "HOME", "XDG_CONFIG_HOME", "TMPDIR", "LANG", "LC_ALL", "LC_CTYPE", "ASPELL_CONF",
            };
        var variables = new List<KeyValuePair<string, string>>(names.Length);
        foreach (var name in names)
        {
            if (environment.GetVariable(name) is { } value)
            {
                variables.Add(new KeyValuePair<string, string>(name, value));
            }
        }

        return ChildEnvironment.Create(variables);
    }

    private static void ValidateDictionary(string dictionary)
    {
        if (dictionary.Length > 128 || dictionary.Contains('\0', StringComparison.Ordinal) ||
            dictionary.Contains('\r', StringComparison.Ordinal) || dictionary.Contains('\n', StringComparison.Ordinal))
        {
            throw new SpellCheckException("The configured spelling dictionary is not a valid GNU Aspell dictionary name.");
        }
    }

    private static ImmutableArray<SpellInputLine> CreateInputLines(string message)
    {
        var result = ImmutableArray.CreateBuilder<SpellInputLine>();
        var lineStart = 0;
        while (lineStart <= message.Length)
        {
            var lineEnd = message.IndexOf('\n', lineStart);
            if (lineEnd < 0)
            {
                lineEnd = message.Length;
            }

            var lineLength = lineEnd - lineStart;
            if (lineLength > 0 && message[lineEnd - 1] == '\r')
            {
                lineLength--;
            }

            var line = message.Substring(lineStart, lineLength);
            if (line.Length > 0 && !SignedOffByPattern().IsMatch(line))
            {
                result.Add(new SpellInputLine(lineStart, line));
            }

            if (lineEnd == message.Length)
            {
                break;
            }

            lineStart = lineEnd + 1;
        }

        return result.ToImmutable();
    }

    private static byte[] CreateInput(ImmutableArray<SpellInputLine> lines)
    {
        var builder = new StringBuilder();
        foreach (var line in lines)
        {
            builder.Append('^');
            builder.Append(line.Text);
            builder.Append('\n');
        }

        return s_strictUtf8.GetBytes(builder.ToString());
    }

    private static SpellCheckResult ParseResult(
        long documentVersion,
        string dictionary,
        string message,
        ImmutableArray<SpellInputLine> inputLines,
        string output)
    {
        var outputLines = output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (outputLines.Length == 0 || !TryParseVersion(outputLines[0], out var checkerVersion))
        {
            throw new SpellCheckException("GNU Aspell returned an unrecognized pipe-mode version banner.");
        }

        var issues = ImmutableArray.CreateBuilder<SpellingIssue>();
        var outputIndex = 1;
        foreach (var inputLine in inputLines)
        {
            var foundTerminator = false;
            while (outputIndex < outputLines.Length)
            {
                var response = outputLines[outputIndex++];
                if (response.Length == 0)
                {
                    foundTerminator = true;
                    break;
                }

                if (response is "*" or "+" or "-")
                {
                    continue;
                }

                if (!TryParseIssue(inputLine, response, out var issue))
                {
                    throw new SpellCheckException("GNU Aspell returned an unrecognized pipe-mode response.");
                }

                if (issues.Count == MaximumIssues)
                {
                    throw new SpellCheckException(
                        $"Spelling stopped after {MaximumIssues.ToString(CultureInfo.InvariantCulture)} issues to keep the result bounded.");
                }

                issues.Add(issue);
            }

            if (!foundTerminator)
            {
                throw new SpellCheckException("GNU Aspell ended its pipe response before completing every input line.");
            }
        }

        while (outputIndex < outputLines.Length)
        {
            if (outputLines[outputIndex++].Length != 0)
            {
                throw new SpellCheckException("GNU Aspell returned unexpected data after the final pipe response.");
            }
        }

        foreach (var issue in issues)
        {
            if (issue.Offset < 0 || issue.Length <= 0 || issue.Offset + issue.Length > message.Length ||
                !message.AsSpan(issue.Offset, issue.Length).SequenceEqual(issue.Word.AsSpan()))
            {
                throw new SpellCheckException("GNU Aspell returned a misspelled range outside the checked commit message.");
            }
        }

        return new SpellCheckResult(documentVersion, dictionary, checkerVersion, issues.ToImmutable());
    }

    private static bool TryParseVersion(string banner, out string version)
    {
        version = string.Empty;
        if (!banner.StartsWith("@(#) ", StringComparison.Ordinal))
        {
            return false;
        }

        var match = AspellVersionPattern().Match(banner);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var major) ||
            !int.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var minor) ||
            (major == 0 && minor < 60))
        {
            return false;
        }

        version = SanitizeDisplayText(banner[5..].Trim());
        return version.Length > 0;
    }

    private static bool TryParseIssue(
        SpellInputLine line,
        string response,
        out SpellingIssue issue)
    {
        issue = null!;
        var match = IssuePattern().Match(response);
        if (!match.Success || !int.TryParse(
                match.Groups["offset"].Value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var oneBasedOffset) || oneBasedOffset < 1)
        {
            return false;
        }

        var word = match.Groups["word"].Value;
        var localOffset = ResolveTextOffset(line.Text, oneBasedOffset - 1, word);
        if (localOffset < 0)
        {
            return false;
        }

        var suggestions = match.Groups["suggestions"].Success
            ? match.Groups["suggestions"].Value
                .Split(", ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Take(MaximumSuggestionsPerIssue)
                .Select(SanitizeDisplayText)
                .Where(static suggestion => suggestion.Length > 0)
                .ToImmutableArray()
            : [];
        issue = new SpellingIssue(
            checked(line.DocumentOffset + localOffset),
            word.Length,
            word,
            suggestions);
        return true;
    }

    private static int ResolveTextOffset(string line, int reportedOffset, string word)
    {
        Span<int> candidates = stackalloc int[3];
        candidates[0] = reportedOffset;
        candidates[1] = GetUtf16OffsetFromRuneOffset(line, reportedOffset);
        candidates[2] = GetUtf16OffsetFromUtf8Offset(line, reportedOffset);
        foreach (var candidate in candidates)
        {
            if (candidate >= 0 && candidate + word.Length <= line.Length &&
                line.AsSpan(candidate, word.Length).SequenceEqual(word.AsSpan()))
            {
                return candidate;
            }
        }

        return -1;
    }

    private static int GetUtf16OffsetFromRuneOffset(string text, int runeOffset)
    {
        var offset = 0;
        var count = 0;
        while (offset < text.Length && count < runeOffset)
        {
            if (Rune.DecodeFromUtf16(text.AsSpan(offset), out var rune, out _) != System.Buffers.OperationStatus.Done)
            {
                return -1;
            }

            offset += rune.Utf16SequenceLength;
            count++;
        }

        return count == runeOffset ? offset : -1;
    }

    private static int GetUtf16OffsetFromUtf8Offset(string text, int byteOffset)
    {
        var utf16Offset = 0;
        var consumedBytes = 0;
        while (utf16Offset < text.Length && consumedBytes < byteOffset)
        {
            if (Rune.DecodeFromUtf16(text.AsSpan(utf16Offset), out var rune, out _) != System.Buffers.OperationStatus.Done)
            {
                return -1;
            }

            consumedBytes += rune.Utf8SequenceLength;
            utf16Offset += rune.Utf16SequenceLength;
        }

        return consumedBytes == byteOffset ? utf16Offset : -1;
    }

    private static string DecodeError(ReadOnlySpan<byte> bytes)
    {
        try
        {
            return SanitizeDisplayText(s_strictUtf8.GetString(bytes)).Trim();
        }
        catch (DecoderFallbackException)
        {
            return "GNU Aspell returned an error that is not valid UTF-8.";
        }
    }

    private static string SanitizeDisplayText(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            if (!Rune.IsControl(rune) || rune.Value == '\t')
            {
                builder.Append(rune.ToString());
            }
        }

        return builder.ToString();
    }

    [GeneratedRegex("Aspell ([0-9]+)\\.([0-9]+)", RegexOptions.CultureInvariant)]
    private static partial Regex AspellVersionPattern();

    [GeneratedRegex("^[a-z-]+-by:.*<.*@.*>$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SignedOffByPattern();

    [GeneratedRegex("^(?:&|\\?) (?<word>\\S+) [0-9]+ (?<offset>[0-9]+): (?<suggestions>.*)$|^# (?<word>\\S+) (?<offset>[0-9]+)$", RegexOptions.CultureInvariant)]
    private static partial Regex IssuePattern();
}
