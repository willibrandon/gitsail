namespace GitSail.Git.Execution;

/// <summary>
/// Supplies serialized user decisions for executable configuration requests.
/// </summary>
internal interface IExecutableCapabilityResponder
{
    /// <summary>
    /// Requests one explicit deny, one-time, or repository-persistent decision.
    /// </summary>
    /// <param name="request">The exact command and data-exposure review.</param>
    /// <param name="cancellationToken">Signals review cancellation.</param>
    /// <returns>The explicit decision selected for this exact request.</returns>
    Task<ExecutableCapabilityDecision> RequestAsync(
        ExecutableCapabilityRequest request,
        CancellationToken cancellationToken);
}
