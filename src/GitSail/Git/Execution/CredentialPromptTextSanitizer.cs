using System.Text;

namespace GitSail.Git.Execution;

/// <summary>
/// Makes untrusted Git and SSH prompt text safe for terminal presentation.
/// </summary>
internal static class CredentialPromptTextSanitizer
{
    /// <summary>
    /// Replaces terminal controls and bidirectional overrides while retaining readable text.
    /// </summary>
    /// <param name="value">The untrusted bounded helper prompt.</param>
    /// <returns>The control-safe prompt text.</returns>
    internal static string Sanitize(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var result = new StringBuilder(value.Length);
        foreach (var rune in value.EnumerateRunes())
        {
            if (Rune.IsControl(rune) || IsBidirectionalControl(rune.Value) || rune.Value == 0x7F)
            {
                result.Append($"<U+{rune.Value:X4}>");
            }
            else
            {
                result.Append(rune.ToString());
            }
        }

        return result.ToString();
    }

    private static bool IsBidirectionalControl(int value)
        => value is 0x061C or 0x200E or 0x200F or
            >= 0x202A and <= 0x202E or
            >= 0x2066 and <= 0x2069;
}
