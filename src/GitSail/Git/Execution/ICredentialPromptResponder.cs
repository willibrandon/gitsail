namespace GitSail.Git.Execution;

/// <summary>
/// Supplies queued user responses to authenticated Git and SSH helper requests.
/// </summary>
internal interface ICredentialPromptResponder
{
    /// <summary>
    /// Requests one bounded response without persisting it as a credential.
    /// </summary>
    /// <param name="operation">The transport operation requesting the response.</param>
    /// <param name="prompt">The control-safe prompt text.</param>
    /// <param name="kind">The required response treatment.</param>
    /// <param name="cancellationToken">Signals prompt cancellation.</param>
    /// <returns>Owned UTF-8 response bytes, or <see langword="null"/> when cancelled.</returns>
    Task<byte[]?> RequestAsync(
        string operation,
        string prompt,
        CredentialPromptKind kind,
        CancellationToken cancellationToken);
}
