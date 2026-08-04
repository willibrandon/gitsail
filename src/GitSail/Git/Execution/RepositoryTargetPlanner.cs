using GitSail.Domain;
using System.Text;

namespace GitSail.Git.Execution;

/// <summary>
/// Resolves one new repository directory beneath an existing canonical parent.
/// </summary>
internal sealed class RepositoryTargetPlanner
{
    private static readonly UTF8Encoding s_strictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private readonly CanonicalDirectory _launchDirectory;

    /// <summary>
    /// Initializes target planning relative to one canonical launch directory.
    /// </summary>
    /// <param name="launchDirectory">The base used for relative user input.</param>
    internal RepositoryTargetPlanner(CanonicalDirectory launchDirectory)
    {
        ArgumentNullException.ThrowIfNull(launchDirectory);
        _launchDirectory = launchDirectory;
    }

    /// <summary>
    /// Canonicalizes one target before creation without creating a filesystem entry.
    /// </summary>
    /// <param name="targetDirectory">The absolute or launch-directory-relative user input.</param>
    /// <returns>The exact target, existing canonical parent, and pre-operation existence state.</returns>
    internal RepositoryTargetPlan Prepare(string targetDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectory);
        if (targetDirectory.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("A repository target cannot contain NUL.", nameof(targetDirectory));
        }

        var launchPath = GetManagedPath(_launchDirectory);
        var requestedPath = Path.GetFullPath(targetDirectory, launchPath);
        if (File.Exists(requestedPath))
        {
            throw new IOException("The repository target is an existing file.");
        }

        if (Directory.Exists(requestedPath))
        {
            var existingTarget = CanonicalDirectory.Create(requestedPath);
            var existingCanonicalPath = GetManagedPath(existingTarget);
            var parentPath = Path.GetDirectoryName(
                Path.TrimEndingDirectorySeparator(existingCanonicalPath));
            if (parentPath is null)
            {
                parentPath = existingCanonicalPath;
            }

            return new RepositoryTargetPlan(
                CanonicalDirectory.Create(parentPath),
                CreateNativePath(existingCanonicalPath),
                existingCanonicalPath,
                existedBeforeOperation: true);
        }

        var trimmedPath = Path.TrimEndingDirectorySeparator(requestedPath);
        var leafName = Path.GetFileName(trimmedPath);
        var requestedParentPath = Path.GetDirectoryName(trimmedPath);
        if (leafName.Length == 0 || requestedParentPath is null)
        {
            throw new InvalidDataException("A new repository target must have one existing parent directory.");
        }

        var parentDirectory = CanonicalDirectory.Create(requestedParentPath);
        var canonicalTargetPath = Path.Combine(GetManagedPath(parentDirectory), leafName);
        if (File.Exists(canonicalTargetPath) || Directory.Exists(canonicalTargetPath))
        {
            throw new IOException("The canonical repository target already exists.");
        }

        return new RepositoryTargetPlan(
            parentDirectory,
            CreateNativePath(canonicalTargetPath),
            canonicalTargetPath,
            existedBeforeOperation: false);
    }

    private static string GetManagedPath(CanonicalDirectory directory)
        => directory.Kind == NativePathKind.WindowsUtf16
            ? directory.GetWindowsPath()
            : s_strictUtf8.GetString(directory.GetUnixBytes());

    private static GitPath CreateNativePath(string path)
        => OperatingSystem.IsWindows()
            ? GitPath.FromWindowsPath(path)
            : GitPath.FromUnixBytes(s_strictUtf8.GetBytes(path));
}
