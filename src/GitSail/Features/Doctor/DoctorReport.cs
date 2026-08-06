using System.Collections.Immutable;

namespace GitSail.Features.Doctor;

/// <summary>
/// Contains the complete read-only human and machine diagnostic report.
/// </summary>
/// <param name="Product">The product display name.</param>
/// <param name="Version">The application version.</param>
/// <param name="RuntimeIdentifier">The active runtime identifier.</param>
/// <param name="OperatingSystem">The operating-system description.</param>
/// <param name="Architecture">The process architecture.</param>
/// <param name="NativeAot">Whether dynamic code is unavailable in the Native AOT payload.</param>
/// <param name="CommandPath">The current process command path, when available.</param>
/// <param name="InstallationScope">The detected development or .NET tool scope.</param>
/// <param name="CommandPathStatus">The command shim or executable PATH status.</param>
/// <param name="Terminal">The terminal capability report.</param>
/// <param name="Locale">The locale and encoding report.</param>
/// <param name="Git">The Git resolution and version report.</param>
/// <param name="Repository">The repository discovery and trust report.</param>
/// <param name="DotNetSdk">The .NET SDK used for tool management, when available.</param>
/// <param name="Ssh">The optional SSH executable report.</param>
/// <param name="SshKeygen">The optional OpenSSH key-generation executable report.</param>
/// <param name="Storage">The application-owned directory report.</param>
/// <param name="ConfigurationSources">The distinct visible Git configuration sources without values.</param>
/// <param name="ConfigurationSourcesTruncated">Whether the bounded configuration-source list was truncated.</param>
/// <param name="ConfigurationError">The configuration enumeration error, when present.</param>
/// <param name="SymbolLookup">The matching-symbol guidance for this build.</param>
internal sealed record DoctorReport(
    string Product,
    string Version,
    string RuntimeIdentifier,
    string OperatingSystem,
    string Architecture,
    bool NativeAot,
    string? CommandPath,
    string InstallationScope,
    string CommandPathStatus,
    DoctorTerminalReport Terminal,
    DoctorLocaleReport Locale,
    DoctorGitReport Git,
    DoctorRepositoryReport Repository,
    DoctorToolReport DotNetSdk,
    DoctorToolReport Ssh,
    DoctorToolReport SshKeygen,
    DoctorStorageReport Storage,
    ImmutableArray<DoctorConfigurationSource> ConfigurationSources,
    bool ConfigurationSourcesTruncated,
    string? ConfigurationError,
    string SymbolLookup);
