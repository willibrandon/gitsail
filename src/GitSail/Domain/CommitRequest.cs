namespace GitSail.Domain;

/// <summary>
/// Contains controlled options for one Git-owned commit transaction.
/// </summary>
internal sealed record CommitRequest
{
    /// <summary>
    /// Initializes one controlled commit request.
    /// </summary>
    /// <param name="message">The editor message supplied to Git.</param>
    /// <param name="amend">Whether Git replaces the current tip commit.</param>
    /// <param name="signoff">Whether Git appends the committer signoff trailer.</param>
    /// <param name="author">The explicit author identity, or <see langword="null"/>.</param>
    /// <param name="cleanupMode">The documented Git cleanup policy.</param>
    /// <param name="skipHooks">Whether Git bypasses its bypassable commit hooks.</param>
    /// <param name="signCommit">Whether Git signs the resulting commit.</param>
    /// <param name="signingKey">The explicit signing key, or <see langword="null"/> for Git's default.</param>
    /// <param name="confirmedPublishedAmendWarning">The exact local publication warning the user confirmed.</param>
    /// <param name="confirmedDetachedHeadWarning">The exact detached HEAD warning the user confirmed.</param>
    internal CommitRequest(
        string message,
        bool amend = false,
        bool signoff = false,
        string? author = null,
        CommitCleanupMode cleanupMode = CommitCleanupMode.Default,
        bool skipHooks = false,
        bool signCommit = false,
        string? signingKey = null,
        PublishedAmendWarning? confirmedPublishedAmendWarning = null,
        DetachedHeadWarning? confirmedDetachedHeadWarning = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        Message = message;
        Amend = amend;
        Signoff = signoff;
        Author = author;
        CleanupMode = cleanupMode;
        SkipHooks = skipHooks;
        SignCommit = signCommit;
        SigningKey = signingKey;
        ConfirmedPublishedAmendWarning = confirmedPublishedAmendWarning;
        ConfirmedDetachedHeadWarning = confirmedDetachedHeadWarning;
    }

    /// <summary>
    /// Gets the editor message supplied to Git.
    /// </summary>
    internal string Message { get; }

    /// <summary>
    /// Gets whether Git replaces the current tip commit.
    /// </summary>
    internal bool Amend { get; }

    /// <summary>
    /// Gets whether Git appends the committer signoff trailer.
    /// </summary>
    internal bool Signoff { get; }

    /// <summary>
    /// Gets the explicit author identity, or <see langword="null"/>.
    /// </summary>
    internal string? Author { get; }

    /// <summary>
    /// Gets the documented Git cleanup policy.
    /// </summary>
    internal CommitCleanupMode CleanupMode { get; }

    /// <summary>
    /// Gets whether Git bypasses its bypassable commit hooks.
    /// </summary>
    internal bool SkipHooks { get; }

    /// <summary>
    /// Gets whether Git signs the resulting commit.
    /// </summary>
    internal bool SignCommit { get; }

    /// <summary>
    /// Gets the explicit signing key, or <see langword="null"/> for Git's default.
    /// </summary>
    internal string? SigningKey { get; }

    /// <summary>
    /// Gets the exact local publication warning the user confirmed before requesting this amend.
    /// </summary>
    internal PublishedAmendWarning? ConfirmedPublishedAmendWarning { get; }

    /// <summary>
    /// Gets the exact detached HEAD warning the user confirmed before requesting this commit.
    /// </summary>
    internal DetachedHeadWarning? ConfirmedDetachedHeadWarning { get; }
}
