namespace GitSail.Domain;

/// <summary>
/// Selects one allowlisted Git merge strategy for a two-head merge.
/// </summary>
internal enum MergeStrategy
{
    /// <summary>
    /// Lets Git choose its documented default strategy.
    /// </summary>
    Default,

    /// <summary>
    /// Uses Git's rename-aware ort strategy.
    /// </summary>
    Ort,

    /// <summary>
    /// Uses Git's two-head resolve strategy without rename handling.
    /// </summary>
    Resolve,

    /// <summary>
    /// Records the other history while retaining the current tree completely.
    /// </summary>
    Ours,

    /// <summary>
    /// Uses Git's subtree-adjusting merge strategy.
    /// </summary>
    Subtree,
}
