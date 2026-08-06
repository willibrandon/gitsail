namespace GitSail.Git.Execution;

/// <summary>
/// Enforces explicit review before any configured executable or shell command may run.
/// </summary>
internal sealed class ExecutableConfigurationBroker
{
    private readonly ExecutableCapabilityGrantStore _grants;
    private readonly IExecutableCapabilityResponder _responder;

    /// <summary>
    /// Initializes the single authorization boundary over persistent grants and controlled prompts.
    /// </summary>
    /// <param name="grants">The current repository's user-global command grants.</param>
    /// <param name="responder">The serialized review decision source.</param>
    internal ExecutableConfigurationBroker(
        ExecutableCapabilityGrantStore grants,
        IExecutableCapabilityResponder responder)
    {
        ArgumentNullException.ThrowIfNull(grants);
        ArgumentNullException.ThrowIfNull(responder);
        _grants = grants;
        _responder = responder;
    }

    /// <summary>
    /// Authorizes one exact request through a current grant or an explicit user decision.
    /// </summary>
    /// <param name="request">The exact command, executable, source, directory, and exposed data.</param>
    /// <param name="cancellationToken">Signals capability review or persistence cancellation.</param>
    /// <returns><see langword="true"/> only when the exact request may run.</returns>
    internal async Task<bool> AuthorizeAsync(
        ExecutableCapabilityRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_grants.IsGranted(request))
        {
            return true;
        }

        var decision = await _responder.RequestAsync(
            request,
            cancellationToken).ConfigureAwait(false);
        return decision switch
        {
            ExecutableCapabilityDecision.Deny => false,
            ExecutableCapabilityDecision.AllowOnce => true,
            ExecutableCapabilityDecision.AllowRepository =>
                await PersistAsync(request, cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidOperationException("The capability responder returned an unknown decision."),
        };
    }

    private async Task<bool> PersistAsync(
        ExecutableCapabilityRequest request,
        CancellationToken cancellationToken)
    {
        await _grants.GrantAsync(request, cancellationToken).ConfigureAwait(false);
        return true;
    }
}
