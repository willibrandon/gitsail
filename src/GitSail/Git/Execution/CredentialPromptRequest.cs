namespace GitSail.Git.Execution;

/// <summary>
/// Describes one bounded authenticated credential prompt waiting for a user response.
/// </summary>
internal sealed class CredentialPromptRequest
{
    /// <summary>
    /// Initializes one operation-labeled prompt with a stable in-process identity.
    /// </summary>
    /// <param name="id">The monotonically increasing request identity.</param>
    /// <param name="operation">The transport operation requesting a response.</param>
    /// <param name="prompt">The control-safe prompt text.</param>
    /// <param name="kind">The required visible, secret, or confirmation treatment.</param>
    internal CredentialPromptRequest(
        long id,
        string operation,
        string prompt,
        CredentialPromptKind kind)
    {
        if (id <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(id));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        Id = id;
        Operation = operation;
        Prompt = prompt;
        Kind = kind;
    }

    /// <summary>
    /// Gets the stable in-process identity used to reconcile one prompt window.
    /// </summary>
    internal long Id { get; }

    /// <summary>
    /// Gets the transport operation that requested the response.
    /// </summary>
    internal string Operation { get; }

    /// <summary>
    /// Gets the bounded control-safe text supplied by Git or SSH.
    /// </summary>
    internal string Prompt { get; }

    /// <summary>
    /// Gets whether the response is visible text, a secret, or a confirmation.
    /// </summary>
    internal CredentialPromptKind Kind { get; }
}
