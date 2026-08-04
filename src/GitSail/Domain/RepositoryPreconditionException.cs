namespace GitSail.Domain;

/// <summary>
/// Reports that repository identity changed after an action was prepared.
/// </summary>
internal sealed class RepositoryPreconditionException : InvalidOperationException
{
    /// <summary>
    /// Initializes one repository precondition failure.
    /// </summary>
    /// <param name="message">The actionable precondition description.</param>
    internal RepositoryPreconditionException(string message)
        : base(message)
    {
    }
}
