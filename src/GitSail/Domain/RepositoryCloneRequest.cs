namespace GitSail.Domain;

/// <summary>
/// Describes one exact repository clone requested from the chooser.
/// </summary>
internal sealed class RepositoryCloneRequest
{
    /// <summary>
    /// Initializes one validated clone request.
    /// </summary>
    /// <param name="source">The local path or remote URL passed to Git as one literal argument.</param>
    /// <param name="targetDirectory">The user-entered target directory resolved from the chooser's launch directory.</param>
    /// <param name="mode">The selected local-object behavior.</param>
    /// <param name="recurseSubmodules">Whether Git initializes and recursively clones all active submodules.</param>
    internal RepositoryCloneRequest(
        string source,
        string targetDirectory,
        RepositoryCloneMode mode,
        bool recurseSubmodules)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectory);
        if (source.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("A clone source cannot contain NUL.", nameof(source));
        }

        if (targetDirectory.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("A clone target cannot contain NUL.", nameof(targetDirectory));
        }

        Source = source;
        TargetDirectory = targetDirectory;
        Mode = mode;
        RecurseSubmodules = recurseSubmodules;
    }

    /// <summary>
    /// Gets the local path or remote URL passed to Git as one literal argument.
    /// </summary>
    internal string Source { get; }

    /// <summary>
    /// Gets the user-entered target directory.
    /// </summary>
    internal string TargetDirectory { get; }

    /// <summary>
    /// Gets the selected local-object behavior.
    /// </summary>
    internal RepositoryCloneMode Mode { get; }

    /// <summary>
    /// Gets whether Git initializes and recursively clones active submodules.
    /// </summary>
    internal bool RecurseSubmodules { get; }
}
