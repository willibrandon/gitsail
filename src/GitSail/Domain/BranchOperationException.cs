namespace GitSail.Domain;

/// <summary>
/// Reports an expected branch-action refusal that should be presented without terminating the session.
/// </summary>
internal sealed class BranchOperationException : InvalidOperationException
{
    /// <summary>
    /// Initializes one actionable branch-operation refusal.
    /// </summary>
    /// <param name="message">The control-safe refusal explanation.</param>
    internal BranchOperationException(string message)
        : base(message)
    {
    }
}
