using System.Text;

namespace GitSail.Domain;

/// <summary>
/// Validates complete user-defined tool drafts before configuration mutation.
/// </summary>
internal static class ConfiguredToolConfigurationValidator
{
    private const int MaximumNameCharacters = 512;
    private const int MaximumTitleCharacters = 512;
    private const int MaximumPromptCharacters = 4096;
    private const int MaximumCommandCharacters = 256 * 1024;
    private static readonly UTF8Encoding s_strictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    /// <summary>
    /// Validates every field and its resulting concrete Git configuration keys.
    /// </summary>
    /// <param name="configuration">The complete proposed tool configuration.</param>
    /// <param name="error">The actionable validation error, when invalid.</param>
    /// <returns><see langword="true"/> when every field can be written exactly.</returns>
    internal static bool TryValidate(
        ConfiguredToolConfiguration configuration,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (!TryValidateName(configuration.Name, out error))
        {
            return false;
        }

        if (configuration.Command.Length is 0 or > MaximumCommandCharacters)
        {
            error = $"Command must contain between 1 and {MaximumCommandCharacters} characters.";
            return false;
        }

        if (configuration.Title.Length > MaximumTitleCharacters)
        {
            error = $"Title cannot exceed {MaximumTitleCharacters} characters.";
            return false;
        }

        if (configuration.Prompt.Length > MaximumPromptCharacters ||
            configuration.ArgumentPrompt.Length > MaximumPromptCharacters ||
            configuration.RevisionPrompt.Length > MaximumPromptCharacters)
        {
            error = $"Tool prompts cannot exceed {MaximumPromptCharacters} characters.";
            return false;
        }

        try
        {
            _ = s_strictUtf8.GetByteCount(configuration.Command);
            _ = s_strictUtf8.GetByteCount(configuration.Title);
            _ = s_strictUtf8.GetByteCount(configuration.Prompt);
            _ = s_strictUtf8.GetByteCount(configuration.ArgumentPrompt);
            _ = s_strictUtf8.GetByteCount(configuration.RevisionPrompt);
        }
        catch (EncoderFallbackException)
        {
            error = "Tool text must contain valid Unicode.";
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>
    /// Validates one subsection name against every supported concrete configured-tool key.
    /// </summary>
    /// <param name="name">The exact proposed configured-tool subsection name.</param>
    /// <param name="error">The actionable validation error, when invalid.</param>
    /// <returns><see langword="true"/> when every concrete property key is valid.</returns>
    internal static bool TryValidateName(string name, out string? error)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Length is 0 or > MaximumNameCharacters)
        {
            error = $"Tool name must contain between 1 and {MaximumNameCharacters} characters.";
            return false;
        }

        try
        {
            foreach (var property in ConfiguredToolConfigurationProperties.All)
            {
                _ = GitConfigurationKey.FromBytes(s_strictUtf8.GetBytes(
                    $"guitool.{name}.{property}"));
            }
        }
        catch (Exception exception) when (exception is ArgumentException or EncoderFallbackException)
        {
            error = $"Tool name cannot form a Git configuration key: {exception.Message}";
            return false;
        }

        error = null;
        return true;
    }
}
