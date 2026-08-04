namespace GitSail.Git.Execution;

/// <summary>
/// Resolves platform configuration and cache directories from explicitly classified environment values.
/// </summary>
internal sealed class UserDirectoryPathService
{
    private const string ApplicationDirectoryName = "gitsail";
    private readonly IProcessEnvironment _environment;

    /// <summary>
    /// Initializes platform user-directory resolution over one explicit environment source.
    /// </summary>
    /// <param name="environment">The classified process-environment source.</param>
    internal UserDirectoryPathService(IProcessEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        _environment = environment;
    }

    /// <summary>
    /// Resolves the application configuration directory using platform-standard precedence.
    /// </summary>
    /// <returns>The fully qualified application configuration directory.</returns>
    internal string GetConfigurationDirectory()
    {
        if (_environment.IsWindows)
        {
            var root = GetAbsoluteVariable("APPDATA") ??
                GetAbsoluteVariable("LOCALAPPDATA") ??
                throw new InvalidOperationException("APPDATA or LOCALAPPDATA is required for user configuration.");
            return CombineAbsolute(root, ApplicationDirectoryName);
        }

        var home = GetUnixHome();
        if (OperatingSystem.IsMacOS())
        {
            return CombineAbsolute(home, "Library", "Application Support", ApplicationDirectoryName);
        }

        var configurationRoot = GetAbsoluteVariable("XDG_CONFIG_HOME") ??
            CombineAbsolute(home, ".config");
        return CombineAbsolute(configurationRoot, ApplicationDirectoryName);
    }

    /// <summary>
    /// Resolves the application cache directory using platform-standard precedence.
    /// </summary>
    /// <returns>The fully qualified application cache directory.</returns>
    internal string GetCacheDirectory()
    {
        if (_environment.IsWindows)
        {
            var root = GetAbsoluteVariable("LOCALAPPDATA") ??
                throw new InvalidOperationException("LOCALAPPDATA is required for user cache storage.");
            return CombineAbsolute(root, ApplicationDirectoryName, "cache");
        }

        var home = GetUnixHome();
        if (OperatingSystem.IsMacOS())
        {
            return CombineAbsolute(home, "Library", "Caches", ApplicationDirectoryName);
        }

        var cacheRoot = GetAbsoluteVariable("XDG_CACHE_HOME") ??
            CombineAbsolute(home, ".cache");
        return CombineAbsolute(cacheRoot, ApplicationDirectoryName);
    }

    /// <summary>
    /// Resolves the application state directory using platform-standard precedence.
    /// </summary>
    /// <returns>The fully qualified application state directory.</returns>
    internal string GetStateDirectory()
    {
        if (_environment.IsWindows)
        {
            var root = GetAbsoluteVariable("LOCALAPPDATA") ??
                throw new InvalidOperationException("LOCALAPPDATA is required for user state storage.");
            return CombineAbsolute(root, ApplicationDirectoryName, "state");
        }

        var home = GetUnixHome();
        if (OperatingSystem.IsMacOS())
        {
            return CombineAbsolute(home, "Library", "Application Support", ApplicationDirectoryName, "state");
        }

        var stateRoot = GetAbsoluteVariable("XDG_STATE_HOME") ??
            CombineAbsolute(home, ".local", "state");
        return CombineAbsolute(stateRoot, ApplicationDirectoryName);
    }

    private string GetUnixHome()
        => GetAbsoluteVariable("HOME") ??
            throw new InvalidOperationException("HOME is required for user directory storage.");

    private string? GetAbsoluteVariable(string name)
    {
        var value = _environment.GetVariable(name);
        return string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value)
            ? null
            : Path.GetFullPath(value);
    }

    private static string CombineAbsolute(string root, params string[] components)
    {
        var path = components.Aggregate(root, Path.Combine);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new InvalidDataException("A resolved user directory must be fully qualified.");
        }

        return Path.GetFullPath(path);
    }
}
