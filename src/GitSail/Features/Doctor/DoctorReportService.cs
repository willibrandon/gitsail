using GitSail.Git.Execution;
using GitSail.Git.Parsing;
using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace GitSail.Features.Doctor;

/// <summary>
/// Collects one bounded read-only diagnostic report from explicit environment and Git services.
/// </summary>
internal static class DoctorReportService
{
    private const int MaximumConfigurationSources = 256;

    /// <summary>
    /// Creates the complete diagnostic report without changing repository or user state.
    /// </summary>
    /// <param name="environment">The classified process environment.</param>
    /// <param name="workingDirectory">The canonical directory used for Git discovery.</param>
    /// <param name="cancellationToken">Signals diagnostic cancellation.</param>
    /// <returns>The complete bounded diagnostic report.</returns>
    internal static async Task<DoctorReport> CreateAsync(
        IProcessEnvironment environment,
        CanonicalDirectory workingDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(workingDirectory);
        var resolver = new ExecutableResolver(environment);
        var runner = new ChildProcessRunner();
        var environmentFactory = new GitChildEnvironmentFactory(environment);
        GitInstallation? installation = null;
        string? gitError = null;
        try
        {
            installation = await new GitVersionService(resolver, runner)
                .GetAsync(workingDirectory, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (IsExpectedDiagnosticFailure(exception))
        {
            gitError = exception.Message;
        }

        var repository = await CreateRepositoryReportAsync(
            installation,
            runner,
            environmentFactory,
            workingDirectory,
            cancellationToken).ConfigureAwait(false);
        var (configurationSources, configurationTruncated, configurationError) =
            await LoadConfigurationSourcesAsync(
                installation,
                runner,
                environmentFactory,
                workingDirectory,
                cancellationToken).ConfigureAwait(false);
        var dotNetSdk = await CreateVersionedToolReportAsync(
            resolver,
            runner,
            environment,
            workingDirectory,
            ProgramKind.DotNet,
            "dotnetSdk",
            cancellationToken).ConfigureAwait(false);
        var processPath = Environment.ProcessPath;
        return new DoctorReport(
            BuildInformation.ProductName,
            BuildInformation.Version,
            RuntimeInformation.RuntimeIdentifier,
            RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString(),
            !RuntimeFeature.IsDynamicCodeSupported,
            processPath,
            GetInstallationScope(processPath),
            GetCommandPathStatus(environment, processPath),
            CreateTerminalReport(environment),
            CreateLocaleReport(),
            CreateGitReport(installation, gitError),
            repository,
            dotNetSdk,
            CreateOptionalToolReport(resolver, ProgramKind.Ssh, "ssh"),
            CreateOptionalToolReport(resolver, ProgramKind.SshKeygen, "sshKeygen"),
            CreateStorageReport(environment),
            configurationSources,
            configurationTruncated,
            configurationError,
            $"Use retained symbols for {BuildInformation.DisplayVersion} on {RuntimeInformation.RuntimeIdentifier}; " +
                "the release build-ID manifest selects the matching native symbol file.");
    }

    private static DoctorTerminalReport CreateTerminalReport(IProcessEnvironment environment)
    {
        var inputRedirected = Console.IsInputRedirected;
        var outputRedirected = Console.IsOutputRedirected;
        int? width = null;
        int? height = null;
        if (!inputRedirected && !outputRedirected)
        {
            try
            {
                width = Console.WindowWidth;
                height = Console.WindowHeight;
            }
            catch (IOException)
            {
            }
        }

        var description = width is null || height is null
            ? "redirected"
            : $"{width}x{height}";
        return new DoctorTerminalReport(
            description,
            inputRedirected,
            outputRedirected,
            width,
            height,
            GetColorCapability(environment, outputRedirected),
            inputRedirected ? "unavailable while redirected" : "terminal key input",
            outputRedirected ? "unavailable while redirected" : "enabled by GitSail",
            Console.OutputEncoding.WebName,
            outputRedirected ? "unavailable while redirected" : "OSC 52; terminal support is not probed");
    }

    private static DoctorLocaleReport CreateLocaleReport()
        => new(
            GetCultureName(CultureInfo.CurrentCulture),
            GetCultureName(CultureInfo.CurrentUICulture),
            Console.InputEncoding.WebName,
            Console.OutputEncoding.WebName,
            GetGlobalizationCapability());

    private static DoctorGitReport CreateGitReport(
        GitInstallation? installation,
        string? error)
        => installation is null
            ? new DoctorGitReport(false, null, null, false, [], error)
            : new DoctorGitReport(
                true,
                installation.Executable.Path,
                installation.Version.ToString(),
                IsGitVersionAtLeast(installation.Version, 2, 36),
                [
                    new DoctorCapabilityReport(
                        "porcelain-v2 status",
                        IsGitVersionAtLeast(installation.Version, 2, 11),
                        "Git 2.11"),
                    new DoctorCapabilityReport(
                        "pathspec-from-file",
                        IsGitVersionAtLeast(installation.Version, 2, 25),
                        "Git 2.25"),
                    new DoctorCapabilityReport(
                        "SHA-256 repositories",
                        IsGitVersionAtLeast(installation.Version, 2, 29),
                        "Git 2.29"),
                    new DoctorCapabilityReport(
                        "maintenance command",
                        IsGitVersionAtLeast(installation.Version, 2, 30),
                        "Git 2.30"),
                ],
                null);

    private static async Task<DoctorRepositoryReport> CreateRepositoryReportAsync(
        GitInstallation? installation,
        ChildProcessRunner runner,
        GitChildEnvironmentFactory environmentFactory,
        CanonicalDirectory workingDirectory,
        CancellationToken cancellationToken)
    {
        if (installation is null)
        {
            return new DoctorRepositoryReport(
                false,
                null,
                null,
                null,
                null,
                "not established",
                "Git is unavailable.");
        }

        try
        {
            var repository = await new RepositoryDiscoveryService(
                installation,
                runner,
                environmentFactory).DiscoverAsync(
                    workingDirectory,
                    cancellationToken).ConfigureAwait(false);
            return new DoctorRepositoryReport(
                true,
                repository.WorkTree?.DisplayText,
                repository.GitDirectory.DisplayText,
                repository.IsBare,
                repository.ObjectFormat.ToString().ToLowerInvariant(),
                "accepted by Git discovery",
                null);
        }
        catch (Exception exception) when (IsExpectedDiagnosticFailure(exception))
        {
            return new DoctorRepositoryReport(
                false,
                null,
                null,
                null,
                null,
                "not established",
                exception.Message);
        }
    }

    private static async Task<(ImmutableArray<DoctorConfigurationSource> Sources, bool Truncated, string? Error)>
        LoadConfigurationSourcesAsync(
            GitInstallation? installation,
            IChildProcessRunner runner,
            GitChildEnvironmentFactory environmentFactory,
            CanonicalDirectory workingDirectory,
            CancellationToken cancellationToken)
    {
        if (installation is null)
        {
            return ([], false, "Git is unavailable.");
        }

        try
        {
            var entries = await new GitConfigurationService(
                installation,
                runner,
                environmentFactory,
                new GitConfigurationParser()).LoadAsync(
                    workingDirectory,
                    cancellationToken).ConfigureAwait(false);
            var sources = entries
                .Select(static entry => new DoctorConfigurationSource(
                    entry.Scope.ToString().ToLowerInvariant(),
                    Encoding.UTF8.GetString(entry.Origin.GetBytes())))
                .Distinct()
                .OrderBy(static source => source.Scope, StringComparer.Ordinal)
                .ThenBy(static source => source.Origin, StringComparer.Ordinal)
                .ToImmutableArray();
            return (
                [.. sources.Take(MaximumConfigurationSources)],
                sources.Length > MaximumConfigurationSources,
                null);
        }
        catch (Exception exception) when (IsExpectedDiagnosticFailure(exception))
        {
            return ([], false, exception.Message);
        }
    }

    private static DoctorToolReport CreateOptionalToolReport(
        ExecutableResolver resolver,
        ProgramKind kind,
        string name)
    {
        try
        {
            var executable = resolver.Resolve(kind);
            return new DoctorToolReport(name, true, executable.Path, null, null);
        }
        catch (Exception exception) when (IsExpectedDiagnosticFailure(exception))
        {
            return new DoctorToolReport(name, false, null, null, exception.Message);
        }
    }

    private static async Task<DoctorToolReport> CreateVersionedToolReportAsync(
        ExecutableResolver resolver,
        ChildProcessRunner runner,
        IProcessEnvironment environment,
        CanonicalDirectory workingDirectory,
        ProgramKind kind,
        string name,
        CancellationToken cancellationToken)
    {
        ResolvedExecutable executable;
        try
        {
            executable = resolver.Resolve(kind);
        }
        catch (Exception exception) when (IsExpectedDiagnosticFailure(exception))
        {
            return new DoctorToolReport(name, false, null, null, exception.Message);
        }

        try
        {
            var invocation = new ProcessInvocation(
                executable,
                [ProcessArgument.Literal("--version")],
                workingDirectory,
                CreateToolEnvironment(environment),
                StandardInputSource.Empty(),
                OutputPolicy.Create(64 * 1024, 64 * 1024));
            var result = await runner.RunAsync(invocation, cancellationToken).ConfigureAwait(false);
            var output = Encoding.UTF8.GetString(result.StandardOutput.Span).Trim();
            if (result.ExitCode == 0 && !string.IsNullOrEmpty(output))
            {
                return new DoctorToolReport(name, true, executable.Path, output, null);
            }

            var error = Encoding.UTF8.GetString(result.StandardError.Span).Trim();
            return new DoctorToolReport(
                name,
                false,
                executable.Path,
                null,
                string.IsNullOrEmpty(error)
                    ? $"The version query exited with code {result.ExitCode}."
                    : error);
        }
        catch (Exception exception) when (IsExpectedDiagnosticFailure(exception))
        {
            return new DoctorToolReport(name, false, executable.Path, null, exception.Message);
        }
    }

    private static ChildEnvironment CreateToolEnvironment(IProcessEnvironment environment)
    {
        var variables = new Dictionary<string, string>(
            environment.IsWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
        {
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
            ["DOTNET_NOLOGO"] = "1",
            ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1",
            ["DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE"] = "1",
        };
        foreach (var name in new[]
        {
            "DOTNET_ROOT",
            "DOTNET_ROOT_X64",
            "DOTNET_ROOT_X86",
            "DOTNET_ROOT_ARM64",
            "HOME",
            "USERPROFILE",
            "SystemRoot",
            "WINDIR",
            "TMPDIR",
            "TEMP",
            "TMP",
        })
        {
            if (environment.GetVariable(name) is { } value)
            {
                variables[name] = value;
            }
        }

        return ChildEnvironment.Create(variables);
    }

    private static DoctorStorageReport CreateStorageReport(IProcessEnvironment environment)
    {
        try
        {
            var paths = new UserDirectoryPathService(environment);
            var configuration = paths.GetConfigurationDirectory();
            var cache = paths.GetCacheDirectory();
            var state = paths.GetStateDirectory();
            return new DoctorStorageReport(
                CreatePathReport("configuration", configuration),
                CreatePathReport("cache", cache),
                CreatePathReport("state", state),
                CreatePathReport("traces", Path.Combine(state, "traces")),
                null);
        }
        catch (Exception exception) when (IsExpectedDiagnosticFailure(exception))
        {
            return new DoctorStorageReport(
                new DoctorPathReport("configuration", null, "unavailable"),
                new DoctorPathReport("cache", null, "unavailable"),
                new DoctorPathReport("state", null, "unavailable"),
                new DoctorPathReport("traces", null, "unavailable"),
                exception.Message);
        }
    }

    private static DoctorPathReport CreatePathReport(string name, string path)
    {
        var information = new DirectoryInfo(path);
        information.Refresh();
        if (!information.Exists)
        {
            return new DoctorPathReport(name, path, "not created");
        }

        if (information.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            return new DoctorPathReport(name, path, "reparse point");
        }

        if (OperatingSystem.IsWindows())
        {
            return new DoctorPathReport(name, path, "directory");
        }

        var mode = Convert.ToString((int)File.GetUnixFileMode(path), 8).PadLeft(3, '0');
        return new DoctorPathReport(name, path, $"directory; mode {mode}");
    }

    private static string GetColorCapability(
        IProcessEnvironment environment,
        bool outputRedirected)
    {
        if (outputRedirected)
        {
            return "unavailable while redirected";
        }

        if (environment.GetVariable("NO_COLOR") is not null)
        {
            return "monochrome (NO_COLOR)";
        }

        var colorTerm = environment.GetVariable("COLORTERM");
        if (string.Equals(colorTerm, "truecolor", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(colorTerm, "24bit", StringComparison.OrdinalIgnoreCase))
        {
            return "truecolor";
        }

        var term = environment.GetVariable("TERM");
        if (term?.Contains("256color", StringComparison.OrdinalIgnoreCase) == true)
        {
            return "256-color";
        }

        return string.Equals(term, "dumb", StringComparison.OrdinalIgnoreCase)
            ? "monochrome (TERM=dumb)"
            : "basic color";
    }

    private static string GetInstallationScope(string? processPath)
    {
        if (processPath is null)
        {
            return "unknown";
        }

        var normalized = processPath.Replace('\\', '/');
        if (normalized.Contains("/.dotnet/tools/.store/gitsail/", StringComparison.OrdinalIgnoreCase))
        {
            return "global .NET tool";
        }

        if (normalized.Contains("/.store/gitsail/", StringComparison.OrdinalIgnoreCase))
        {
            return ".NET tool store";
        }

        return string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase)
            ? "framework-dependent development host"
            : "direct executable";
    }

    private static string GetCommandPathStatus(
        IProcessEnvironment environment,
        string? processPath)
        => GetCommandPathStatus(environment, processPath, OperatingSystem.IsWindows());

    /// <summary>
    /// Reports whether the installed command form for a target platform is available on PATH.
    /// </summary>
    /// <param name="environment">The classified process environment.</param>
    /// <param name="processPath">The current application process path.</param>
    /// <param name="isWindows">Whether to apply the Windows executable contract.</param>
    /// <returns>The command availability diagnostic.</returns>
    internal static string GetCommandPathStatus(
        IProcessEnvironment environment,
        string? processPath,
        bool isWindows)
    {
        if (processPath is null)
        {
            return "current process path is unavailable";
        }

        if (!File.Exists(processPath))
        {
            return "current process path no longer exists";
        }

        var path = environment.GetVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return "current process exists; PATH is empty";
        }

        var fileName = isWindows ? "git-tui.exe" : "git-tui";
        foreach (var directory in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory))
            {
                continue;
            }

            var candidate = Path.Combine(directory, fileName);
            if (File.Exists(candidate))
            {
                return $"available on PATH at {Path.GetFullPath(candidate)}";
            }
        }

        return "current process exists; git-tui was not found on PATH";
    }

    private static string GetGlobalizationCapability()
    {
        try
        {
            _ = CultureInfo.GetCultureInfo("en-US").CompareInfo.Compare("a", "A", CompareOptions.IgnoreCase);
            return OperatingSystem.IsWindows()
                ? "available through Windows globalization"
                : "available through system ICU";
        }
        catch (CultureNotFoundException)
        {
            return "invariant globalization only";
        }
    }

    private static string GetCultureName(CultureInfo culture)
        => string.IsNullOrEmpty(culture.Name) ? "invariant" : culture.Name;

    private static bool IsGitVersionAtLeast(
        GitVersion version,
        int major,
        int minor)
        => version.Major > major || (version.Major == major && version.Minor >= minor);

    private static bool IsExpectedDiagnosticFailure(Exception exception)
        => exception is ArgumentException or
            ExecutableResolutionException or
            GitCommandException or
            InvalidDataException or
            InvalidOperationException or
            IOException or
            UnauthorizedAccessException or
            NotSupportedException;
}
