namespace GitSail.Domain;

/// <summary>
/// Reports that committing on detached HEAD requires explicit user confirmation.
/// </summary>
internal sealed class DetachedHeadConfirmationException : InvalidOperationException
{
    /// <summary>
    /// Initializes a confirmation failure with the exact detached HEAD warning.
    /// </summary>
    /// <param name="warning">The exact detached HEAD commit requiring confirmation.</param>
    internal DetachedHeadConfirmationException(DetachedHeadWarning warning)
        : base(CreateMessage(warning))
    {
        Warning = warning;
    }

    /// <summary>
    /// Gets the exact detached HEAD warning requiring confirmation.
    /// </summary>
    internal DetachedHeadWarning Warning { get; }

    private static string CreateMessage(DetachedHeadWarning warning)
    {
        ArgumentNullException.ThrowIfNull(warning);
        return $"Committing on detached HEAD {warning.HeadObjectId} requires confirmation because the new commit will not belong to a branch.";
    }
}
