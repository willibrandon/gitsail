namespace GitSail.Domain;

/// <summary>
/// Reports a safe actionable linked-worktree validation or Git command failure.
/// </summary>
internal sealed class WorktreeOperationException : Exception
{
    /// <summary>
    /// Initializes one linked-worktree failure without exposing raw control characters.
    /// </summary>
    /// <param name="message">The actionable control-safe failure text.</param>
    internal WorktreeOperationException(string message)
        : base(message)
    {
    }
}
