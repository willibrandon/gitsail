namespace GitSail.Domain;

/// <summary>
/// Retains a local branch name accepted and normalized by Git itself.
/// </summary>
internal sealed class ValidatedBranchName
{
    /// <summary>
    /// Initializes one Git-validated local branch name.
    /// </summary>
    /// <param name="shortName">The exact normalized short branch name.</param>
    /// <param name="fullName">The exact normalized ref below <c>refs/heads/</c>.</param>
    internal ValidatedBranchName(RefName shortName, RefName fullName)
    {
        ArgumentNullException.ThrowIfNull(shortName);
        ArgumentNullException.ThrowIfNull(fullName);
        ShortName = shortName;
        FullName = fullName;
    }

    /// <summary>
    /// Gets the exact normalized short branch name.
    /// </summary>
    internal RefName ShortName { get; }

    /// <summary>
    /// Gets the exact normalized ref below <c>refs/heads/</c>.
    /// </summary>
    internal RefName FullName { get; }
}
