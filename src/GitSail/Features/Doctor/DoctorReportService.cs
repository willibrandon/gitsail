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
            CreateTerminalReport(environment),
            CreateLocaleReport(),
            CreateGitReport(installation, gitError),
            repository,
            CreateOptionalToolReport(resolver, ProgramKind.Ssh, "ssh"),
            CreateStorageReport(environment),
            configurationSources,
            configurationTruncated,
            configurationError,
            $"Use the retained symbols for {BuildInformation.DisplayVersion} and this payload's native build ID.");
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
            outputRedirected ? "unavailable while redirected" : "enabled by GitSail",
            Console.OutputEncoding.WebName);
    }

    private static DoctorLocaleReport CreateLocaleReport()
        => new(
            GetCultureName(CultureInfo.CurrentCulture),
            GetCultureName(CultureInfo.CurrentUICulture),
            Console.InputEncoding.WebName,
            Console.OutputEncoding.WebName);

    private static DoctorGitReport CreateGitReport(
        GitInstallation? installation,
        string? error)
        => installation is null
            ? new DoctorGitReport(false, null, null, false, error)
            : new DoctorGitReport(
                true,
                installation.Executable.Path,
                installation.Version.ToString(),
                installation.Version.Major > 2 ||
                    (installation.Version.Major == 2 && installation.Version.Minor >= 36),
                null);

    private static async Task<DoctorRepositoryReport> CreateRepositoryReportAsync(
        GitInstallation? installation,
        IChildProcessRunner runner,
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
            return new DoctorToolReport(name, true, executable.Path, null);
        }
        catch (Exception exception) when (IsExpectedDiagnosticFailure(exception))
        {
            return new DoctorToolReport(name, false, null, exception.Message);
        }
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

    private static string GetCultureName(CultureInfo culture)
        => string.IsNullOrEmpty(culture.Name) ? "invariant" : culture.Name;

    private static bool IsExpectedDiagnosticFailure(Exception exception)
        => exception is ArgumentException or
            ExecutableResolutionException or
            GitCommandException or
            InvalidDataException or
            IOException or
            UnauthorizedAccessException or
            NotSupportedException;
}
