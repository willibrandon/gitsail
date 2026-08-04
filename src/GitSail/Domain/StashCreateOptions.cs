namespace GitSail.Domain;

/// <summary>
/// Defines one typed noninteractive Git stash-create request.
/// </summary>
internal sealed class StashCreateOptions
{
    /// <summary>
    /// Initializes one validated stash-create request.
    /// </summary>
    /// <param name="message">The optional user-visible stash message.</param>
    /// <param name="fileScope">The tracked, untracked, or ignored file scope.</param>
    /// <param name="keepIndex">Whether staged changes remain in the index and worktree.</param>
    /// <param name="stagedOnly">Whether only staged changes are stashed.</param>
    internal StashCreateOptions(
        string message,
        StashFileScope fileScope,
        bool keepIndex,
        bool stagedOnly)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("A stash message cannot contain NUL.", nameof(message));
        }

        if (!Enum.IsDefined(fileScope))
        {
            throw new ArgumentOutOfRangeException(nameof(fileScope));
        }

        if (stagedOnly && fileScope != StashFileScope.Tracked)
        {
            throw new ArgumentException(
                "A staged-only stash cannot include untracked or ignored paths.",
                nameof(fileScope));
        }

        if (stagedOnly && keepIndex)
        {
            throw new ArgumentException(
                "A staged-only stash already leaves unstaged changes and cannot also request keep-index.",
                nameof(keepIndex));
        }

        Message = message;
        FileScope = fileScope;
        KeepIndex = keepIndex;
        StagedOnly = stagedOnly;
    }

    /// <summary>
    /// Gets the optional user-visible stash message.
    /// </summary>
    internal string Message { get; }

    /// <summary>
    /// Gets the tracked, untracked, or ignored file scope.
    /// </summary>
    internal StashFileScope FileScope { get; }

    /// <summary>
    /// Gets whether staged changes remain in the index and worktree.
    /// </summary>
    internal bool KeepIndex { get; }

    /// <summary>
    /// Gets whether only staged changes are stashed.
    /// </summary>
    internal bool StagedOnly { get; }
}
