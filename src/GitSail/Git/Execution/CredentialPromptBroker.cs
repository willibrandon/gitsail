namespace GitSail.Git.Execution;

/// <summary>
/// Creates isolated one-operation askpass endpoints over one controlled response source.
/// </summary>
internal sealed class CredentialPromptBroker
{
    private readonly ICredentialPromptResponder _responder;
    private readonly string? _helperExecutablePath;

    /// <summary>
    /// Initializes a broker over the running workspace's serialized response coordinator.
    /// </summary>
    /// <param name="responder">The controlled response source.</param>
    /// <param name="helperExecutablePath">An explicit trusted current-executable test seam, or the runtime process path.</param>
    internal CredentialPromptBroker(
        ICredentialPromptResponder responder,
        string? helperExecutablePath = null)
    {
        ArgumentNullException.ThrowIfNull(responder);
        if (helperExecutablePath is not null &&
            (!Path.IsPathFullyQualified(helperExecutablePath) ||
                helperExecutablePath.Contains('\0', StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "The helper executable must be one absolute non-NUL path.",
                nameof(helperExecutablePath));
        }

        _responder = responder;
        _helperExecutablePath = helperExecutablePath;
    }

    /// <summary>
    /// Starts one user-only endpoint with a fresh operation nonce and session identity.
    /// </summary>
    /// <param name="operation">The control-safe transport operation label.</param>
    /// <param name="cancellationToken">Signals operation cancellation.</param>
    /// <returns>The active helper endpoint and child-environment configurator.</returns>
    internal CredentialPromptOperation StartOperation(
        string operation,
        CancellationToken cancellationToken)
        => CredentialPromptOperation.Start(
            _responder,
            operation,
            _helperExecutablePath,
            cancellationToken);
}
