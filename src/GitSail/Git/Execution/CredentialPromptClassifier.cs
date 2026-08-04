namespace GitSail.Git.Execution;

/// <summary>
/// Classifies fixed-locale Git and SSH askpass text without exposing response data.
/// </summary>
internal static class CredentialPromptClassifier
{
    /// <summary>
    /// Classifies one bounded control-safe prompt as visible, secret, or yes/no input.
    /// </summary>
    /// <param name="prompt">The prompt supplied by Git or SSH under the C locale.</param>
    /// <returns>The response treatment required by the prompt.</returns>
    internal static CredentialPromptKind Classify(string prompt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        if (prompt.Contains("yes/no", StringComparison.OrdinalIgnoreCase) ||
            prompt.Contains("are you sure you want to continue connecting", StringComparison.OrdinalIgnoreCase))
        {
            return CredentialPromptKind.Confirmation;
        }

        if (prompt.Contains("username", StringComparison.OrdinalIgnoreCase) ||
            prompt.Contains("user name", StringComparison.OrdinalIgnoreCase) ||
            prompt.Contains("login", StringComparison.OrdinalIgnoreCase))
        {
            return CredentialPromptKind.Text;
        }

        return CredentialPromptKind.Secret;
    }
}
