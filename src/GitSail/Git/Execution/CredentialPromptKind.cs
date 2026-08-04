namespace GitSail.Git.Execution;

/// <summary>
/// Identifies the response treatment required by one transport credential prompt.
/// </summary>
internal enum CredentialPromptKind
{
    /// <summary>
    /// Accepts ordinary visible text such as a user name.
    /// </summary>
    Text,

    /// <summary>
    /// Accepts a secret whose characters must not be rendered or retained.
    /// </summary>
    Secret,

    /// <summary>
    /// Accepts an explicit yes or no response to a transport trust question.
    /// </summary>
    Confirmation,
}
