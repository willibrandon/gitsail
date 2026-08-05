namespace GitSail.Domain;

/// <summary>
/// Maps Git configuration scopes to options write-policy flags.
/// </summary>
internal static class GitConfigurationScopeExtensions
{
    /// <summary>
    /// Converts one concrete Git scope into its write-policy flag.
    /// </summary>
    /// <param name="scope">The Git scope to convert.</param>
    /// <returns>The corresponding write-policy flag, or none for read-only scopes.</returns>
    internal static GitConfigurationScopeMask ToMask(this GitConfigurationScope scope)
        => scope switch
        {
            GitConfigurationScope.Global => GitConfigurationScopeMask.Global,
            GitConfigurationScope.Local => GitConfigurationScopeMask.Local,
            GitConfigurationScope.Worktree => GitConfigurationScopeMask.Worktree,
            _ => GitConfigurationScopeMask.None,
        };
}
