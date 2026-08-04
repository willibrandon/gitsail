using GitSail.Domain;

namespace GitSail.Git.Execution;

/// <summary>
/// Retains one canonical repository-creation target before Git can create it.
/// </summary>
internal sealed class RepositoryTargetPlan
{
    /// <summary>
    /// Initializes one canonical target beneath an existing canonical parent.
    /// </summary>
    /// <param name="parentDirectory">The existing canonical parent directory.</param>
    /// <param name="targetPath">The exact canonical native target path.</param>
    /// <param name="managedTargetPath">The fully qualified managed target path used only for platform filesystem checks.</param>
    /// <param name="existedBeforeOperation">Whether the target directory existed before Git was launched.</param>
    internal RepositoryTargetPlan(
        CanonicalDirectory parentDirectory,
        GitPath targetPath,
        string managedTargetPath,
        bool existedBeforeOperation)
    {
        ArgumentNullException.ThrowIfNull(parentDirectory);
        ArgumentNullException.ThrowIfNull(targetPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(managedTargetPath);
        ParentDirectory = parentDirectory;
        TargetPath = targetPath;
        ManagedTargetPath = managedTargetPath;
        ExistedBeforeOperation = existedBeforeOperation;
    }

    /// <summary>
    /// Gets the existing canonical parent directory.
    /// </summary>
    internal CanonicalDirectory ParentDirectory { get; }

    /// <summary>
    /// Gets the exact canonical native target path.
    /// </summary>
    internal GitPath TargetPath { get; }

    /// <summary>
    /// Gets the fully qualified managed target path used only for platform filesystem checks.
    /// </summary>
    internal string ManagedTargetPath { get; }

    /// <summary>
    /// Gets whether the target directory existed before Git was launched.
    /// </summary>
    internal bool ExistedBeforeOperation { get; }
}
