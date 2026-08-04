namespace GitSail.Domain;

/// <summary>
/// Contains validated typed options for one noninteractive Git merge transaction.
/// </summary>
internal sealed class MergeOptions
{
    /// <summary>
    /// Initializes one immutable merge-option set from allowlisted values.
    /// </summary>
    /// <param name="fastForwardMode">The selected fast-forward policy.</param>
    /// <param name="strategy">The selected allowlisted two-head strategy.</param>
    /// <param name="conflictPreference">The selected ort conflict preference.</param>
    /// <param name="squash">Whether to prepare a squash result without recording merge ancestry.</param>
    /// <param name="stopBeforeCommit">Whether to stop before creating a non-fast-forward merge commit.</param>
    /// <param name="autoStash">The configured or explicit autostash behavior.</param>
    /// <param name="rerereAutoUpdate">The configured or explicit rerere index-update behavior.</param>
    /// <param name="verifySignatures">The configured or explicit incoming-tip signature check.</param>
    internal MergeOptions(
        MergeFastForwardMode fastForwardMode,
        MergeStrategy strategy,
        MergeConflictPreference conflictPreference,
        bool squash,
        bool stopBeforeCommit,
        GitOptionOverride autoStash,
        GitOptionOverride rerereAutoUpdate,
        GitOptionOverride verifySignatures)
    {
        if (!Enum.IsDefined(fastForwardMode))
        {
            throw new ArgumentOutOfRangeException(nameof(fastForwardMode));
        }

        if (!Enum.IsDefined(strategy))
        {
            throw new ArgumentOutOfRangeException(nameof(strategy));
        }

        if (!Enum.IsDefined(conflictPreference))
        {
            throw new ArgumentOutOfRangeException(nameof(conflictPreference));
        }

        if (!Enum.IsDefined(autoStash))
        {
            throw new ArgumentOutOfRangeException(nameof(autoStash));
        }

        if (!Enum.IsDefined(rerereAutoUpdate))
        {
            throw new ArgumentOutOfRangeException(nameof(rerereAutoUpdate));
        }

        if (!Enum.IsDefined(verifySignatures))
        {
            throw new ArgumentOutOfRangeException(nameof(verifySignatures));
        }

        if (squash && stopBeforeCommit)
        {
            throw new ArgumentException("A squash merge already stops without creating a merge commit.");
        }

        if (conflictPreference != MergeConflictPreference.Default &&
            strategy is not MergeStrategy.Default and not MergeStrategy.Ort)
        {
            throw new ArgumentException("The ours/theirs conflict preference requires Git's default or ort strategy.");
        }

        FastForwardMode = fastForwardMode;
        Strategy = strategy;
        ConflictPreference = conflictPreference;
        Squash = squash;
        StopBeforeCommit = stopBeforeCommit;
        AutoStash = autoStash;
        RerereAutoUpdate = rerereAutoUpdate;
        VerifySignatures = verifySignatures;
    }

    /// <summary>
    /// Gets the selected fast-forward policy.
    /// </summary>
    internal MergeFastForwardMode FastForwardMode { get; }

    /// <summary>
    /// Gets the selected allowlisted two-head strategy.
    /// </summary>
    internal MergeStrategy Strategy { get; }

    /// <summary>
    /// Gets the selected ort conflict preference.
    /// </summary>
    internal MergeConflictPreference ConflictPreference { get; }

    /// <summary>
    /// Gets whether Git prepares a squash result without recording merge ancestry.
    /// </summary>
    internal bool Squash { get; }

    /// <summary>
    /// Gets whether Git stops before creating a non-fast-forward merge commit.
    /// </summary>
    internal bool StopBeforeCommit { get; }

    /// <summary>
    /// Gets the configured or explicit autostash behavior.
    /// </summary>
    internal GitOptionOverride AutoStash { get; }

    /// <summary>
    /// Gets the configured or explicit rerere index-update behavior.
    /// </summary>
    internal GitOptionOverride RerereAutoUpdate { get; }

    /// <summary>
    /// Gets the configured or explicit incoming-tip signature check.
    /// </summary>
    internal GitOptionOverride VerifySignatures { get; }

    /// <summary>
    /// Creates the configuration-honoring default merge option set.
    /// </summary>
    /// <returns>A validated option set that adds no semantic overrides.</returns>
    internal static MergeOptions CreateDefault()
        => new(
            MergeFastForwardMode.Default,
            MergeStrategy.Default,
            MergeConflictPreference.Default,
            squash: false,
            stopBeforeCommit: false,
            GitOptionOverride.Configured,
            GitOptionOverride.Configured,
            GitOptionOverride.Configured);
}
