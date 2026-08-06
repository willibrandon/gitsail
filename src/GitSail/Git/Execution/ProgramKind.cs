namespace GitSail.Git.Execution;

/// <summary>
/// Identifies a trusted executable family that GitSail may resolve.
/// </summary>
internal enum ProgramKind
{
    /// <summary>
    /// Identifies the Git command-line executable.
    /// </summary>
    Git,

    /// <summary>
    /// Identifies the OpenSSH-compatible secure-shell executable.
    /// </summary>
    Ssh,

    /// <summary>
    /// Identifies the .NET command used to manage an installed tool.
    /// </summary>
    DotNet,

    /// <summary>
    /// Identifies the optional GNU Aspell command used for commit-message spelling.
    /// </summary>
    Aspell,

    /// <summary>
    /// Identifies the fixed operating-system command interpreter used only after capability approval.
    /// </summary>
    Shell,
}
