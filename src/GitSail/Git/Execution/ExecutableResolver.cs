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
        if (kind == ProgramKind.Shell)
        {
            return ResolvePlatformShell();
        }

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
            ProgramKind.SshKeygen when OperatingSystem.IsWindows() => ["ssh-keygen.exe"],
            ProgramKind.SshKeygen => ["ssh-keygen"],
            ProgramKind.DotNet when OperatingSystem.IsWindows() => ["dotnet.exe"],
            ProgramKind.DotNet => ["dotnet"],
            ProgramKind.Aspell when OperatingSystem.IsWindows() => ["aspell.exe"],
            ProgramKind.Aspell => ["aspell"],
            ProgramKind.Shell => throw new InvalidOperationException(
                "The platform shell is resolved from its fixed operating-system location."),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown executable family."),
        };

    private static string GetDisplayName(ProgramKind kind)
        => kind switch
        {
            ProgramKind.Git => "Git",
            ProgramKind.Ssh => "SSH",
            ProgramKind.SshKeygen => "OpenSSH key generation",
            ProgramKind.DotNet => ".NET",
            ProgramKind.Aspell => "GNU Aspell",
            ProgramKind.Shell => "the platform command interpreter",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown executable family."),
        };

    private ResolvedExecutable ResolvePlatformShell()
    {
        var candidate = OperatingSystem.IsWindows()
            ? GetWindowsShellPath()
            : "/bin/sh";
        if (!TryResolveCandidate(candidate, out var canonicalPath))
        {
            throw new ExecutableResolutionException(
                $"Cannot use {GetDisplayName(ProgramKind.Shell)} at its fixed operating-system location.");
        }

        return new ResolvedExecutable(
            ProgramKind.Shell,
            canonicalPath,
            ExecutableFingerprint.Capture(canonicalPath));
    }

    private string GetWindowsShellPath()
    {
        var windowsDirectory = _environment.GetVariable("SystemRoot") ??
            _environment.GetVariable("WINDIR");
        if (string.IsNullOrWhiteSpace(windowsDirectory) ||
            !Path.IsPathFullyQualified(windowsDirectory) ||
            windowsDirectory.Contains('\0', StringComparison.Ordinal))
        {
            throw new ExecutableResolutionException(
                "Cannot use the platform command interpreter because the Windows system directory is unavailable.");
        }

        return Path.Combine(windowsDirectory, "System32", "cmd.exe");
    }

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
