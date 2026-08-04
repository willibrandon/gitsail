namespace GitSail.CommandLine;

/// <summary>
/// Contains the parsed single-transaction behavior requested for the citool workflow.
/// </summary>
/// <param name="Amend">Whether the workflow begins by amending the current HEAD commit.</param>
/// <param name="NoCommit">Whether successful completion prepares the index without creating a commit.</param>
/// <param name="OpenCommitMessage">Whether initial focus moves directly to the commit-message editor.</param>
internal sealed record CitoolOptions(
    bool Amend,
    bool NoCommit,
    bool OpenCommitMessage);
