namespace GitSail.Git.Execution;

/// <summary>
/// Resolves trusted program kinds without searching the current directory.
/// </summary>
internal sealed class ExecutableResolver
{
    private readonly IProcessEnvironment _environment;

    /// <summary>
    /// Initializes an executable resolver over an explicit process-environment source.
    /// </summary>
    /// <param name="environment">The allowlisted environment source.</param>
    internal ExecutableResolver(IProcessEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        _environment = environment;
    }

    /// <summary>
    /// Resolves one trusted program kind from absolute search-path entries.
    /// </summary>
    /// <param name="kind">The executable family to resolve.</param>
    /// <returns>The canonical executable and its current fingerprint.</returns>
    /// <exception cref="ExecutableResolutionException">The program could not be resolved safely.</exception>
    internal ResolvedExecutable Resolve(ProgramKind kind)
    {
        var searchPath = _environment.GetVariable("PATH");
        if (string.IsNullOrWhiteSpace(searchPath))
        {
            throw new ExecutableResolutionException($"Cannot find {GetDisplayName(kind)} because PATH is empty.");
        }

        foreach (var directory in searchPath.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory))
            {
                continue;
            }

            foreach (var fileName in GetCandidateFileNames(kind))
            {
                var candidate = Path.Combine(directory, fileName);
                if (TryResolveCandidate(candidate, out var canonicalPath))
                {
                    return new ResolvedExecutable(kind, canonicalPath, ExecutableFingerprint.Capture(canonicalPath));
                }
            }
        }

        throw new ExecutableResolutionException(
            $"Cannot find {GetDisplayName(kind)} in an absolute executable directory on PATH.");
    }

    /// <summary>
    /// Determines whether a previously resolved executable still has the captured fingerprint.
    /// </summary>
    /// <param name="executable">The executable to revalidate.</param>
    /// <returns><see langword="true"/> when its canonical file is unchanged; otherwise, <see langword="false"/>.</returns>
    internal static bool IsUnchanged(ResolvedExecutable executable)
    {
        ArgumentNullException.ThrowIfNull(executable);

        try
        {
            return IsExecutableFile(executable.Path) &&
                ExecutableFingerprint.Capture(executable.Path) == executable.Fingerprint;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string[] GetCandidateFileNames(ProgramKind kind)
        => kind switch
        {
            ProgramKind.Git when OperatingSystem.IsWindows() => ["git.exe"],
            ProgramKind.Git => ["git"],
            ProgramKind.Ssh when OperatingSystem.IsWindows() => ["ssh.exe"],
            ProgramKind.Ssh => ["ssh"],
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown executable family."),
        };

    private static string GetDisplayName(ProgramKind kind)
        => kind switch
        {
            ProgramKind.Git => "Git",
            ProgramKind.Ssh => "SSH",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown executable family."),
        };

    private static bool TryResolveCandidate(string candidate, out string canonicalPath)
    {
        canonicalPath = string.Empty;
        try
        {
            if (!IsExecutableFile(candidate))
            {
                return false;
            }

            var information = new FileInfo(candidate);
            var target = information.ResolveLinkTarget(returnFinalTarget: true);
            canonicalPath = Path.GetFullPath(target?.FullName ?? information.FullName);
            return IsExecutableFile(canonicalPath);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsExecutableFile(string path)
    {
        var information = new FileInfo(path);
        information.Refresh();
        if (!information.Exists || (information.Attributes & FileAttributes.Directory) != 0)
        {
            return false;
        }

        if (OperatingSystem.IsWindows())
        {
            return string.Equals(information.Extension, ".exe", StringComparison.OrdinalIgnoreCase);
        }

        var mode = File.GetUnixFileMode(information.FullName);
        const UnixFileMode executeBits = UnixFileMode.UserExecute |
            UnixFileMode.GroupExecute |
            UnixFileMode.OtherExecute;
        return (mode & executeBits) != 0;
    }
}
