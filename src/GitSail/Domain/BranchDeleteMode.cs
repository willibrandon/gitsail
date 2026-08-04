namespace GitSail.Domain;

/// <summary>
/// Selects Git's mergedness policy for deleting a local branch.
/// </summary>
internal enum BranchDeleteMode
{
    /// <summary>
    /// Requires Git to prove the branch is fully merged into an allowed destination.
    /// </summary>
    Safe,

    /// <summary>
    /// Permits deletion without Git's mergedness requirement after destructive confirmation.
    /// </summary>
    Force,
}
