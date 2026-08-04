using GitSail.Domain;
using System.Text;

namespace GitSail.Git.Execution;

/// <summary>
/// Represents an existing canonical absolute directory used as a child working directory.
/// </summary>
internal sealed record CanonicalDirectory
{
    private CanonicalDirectory(string path)
    {
        Path = path;
    }

    /// <summary>
    /// Gets the canonical absolute directory path.
    /// </summary>
    internal string Path { get; }

    /// <summary>
    /// Resolves and validates an existing directory.
    /// </summary>
    /// <param name="path">The absolute directory path to resolve.</param>
    /// <returns>The canonical directory.</returns>
    internal static CanonicalDirectory Create(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!System.IO.Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("A child working directory must be absolute.", nameof(path));
        }

        var information = new DirectoryInfo(path);
        information.Refresh();
        if (!information.Exists)
        {
            throw new DirectoryNotFoundException($"The child working directory does not exist: {path}");
        }

        var target = information.ResolveLinkTarget(returnFinalTarget: true);
        return new CanonicalDirectory(System.IO.Path.GetFullPath(target?.FullName ?? information.FullName));
    }

    /// <summary>
    /// Resolves a discovered native Git path as a canonical child-process directory.
    /// </summary>
    /// <param name="path">The exact discovered directory path.</param>
    /// <returns>The validated canonical directory.</returns>
    internal static CanonicalDirectory Create(GitPath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var directoryPath = path.Kind == NativePathKind.WindowsUtf16
            ? path.GetWindowsPath()
            : new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(path.GetUnixBytes());
        return Create(directoryPath);
    }

    /// <inheritdoc />
    public override string ToString()
        => Path;
}
