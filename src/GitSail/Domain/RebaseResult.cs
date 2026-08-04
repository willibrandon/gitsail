namespace GitSail.Domain;

/// <summary>
/// Reports a completed or recoverably stopped interactive-rebase command.
/// </summary>
/// <param name="Outcome">The classified repository outcome.</param>
/// <param name="State">The current Git-owned rebase state, when stopped.</param>
internal sealed record RebaseResult(RebaseOutcome Outcome, RebaseState? State);
