namespace GitSail.Domain;

/// <summary>
/// Contains validated typed safety, upstream, and tag behavior for one exact push plan.
/// </summary>
internal sealed class PushOptions
{
    /// <summary>
    /// Initializes one immutable push-option set from allowlisted values.
    /// </summary>
    /// <param name="safetyMode">The selected update safety policy.</param>
    /// <param name="setUpstream">Whether successful branch updates explicitly establish upstream tracking.</param>
    /// <param name="followTags">The configured or explicit reachable annotated-tag behavior.</param>
    internal PushOptions(
        PushSafetyMode safetyMode,
        bool setUpstream,
        GitOptionOverride followTags)
    {
        if (!Enum.IsDefined(safetyMode))
        {
            throw new ArgumentOutOfRangeException(nameof(safetyMode));
        }

        if (!Enum.IsDefined(followTags))
        {
            throw new ArgumentOutOfRangeException(nameof(followTags));
        }

        SafetyMode = safetyMode;
        SetUpstream = setUpstream;
        FollowTags = followTags;
    }

    /// <summary>
    /// Gets the selected normal, explicit-lease, or unleased-force policy.
    /// </summary>
    internal PushSafetyMode SafetyMode { get; }

    /// <summary>
    /// Gets whether successful branch updates establish upstream tracking.
    /// </summary>
    internal bool SetUpstream { get; }

    /// <summary>
    /// Gets configured, enabled, or disabled reachable annotated-tag behavior.
    /// </summary>
    internal GitOptionOverride FollowTags { get; }
}
