using GitSail.Domain;
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

namespace GitSail.Git.Execution;

/// <summary>
/// Describes and fingerprints one exact executable-configuration capability review.
/// </summary>
internal sealed class ExecutableCapabilityRequest
{
    private const int MaximumCommandCharacters = 256 * 1024;
    private const int MaximumConfigurationKeyCharacters = 1024;
    private const int MaximumExposureCount = 32;
    private const int MaximumExposureCharacters = 256;
    private static readonly byte[] s_hashDomain =
        "GitSail executable capability v1\0"u8.ToArray();
    private static readonly UTF8Encoding s_utf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    /// <summary>
    /// Initializes one review and computes its stable repository-scoped command hash.
    /// </summary>
    /// <param name="kind">The configured executable behavior family.</param>
    /// <param name="configurationKey">The exact concrete Git configuration key.</param>
    /// <param name="sourceScope">The effective source scope reported by Git.</param>
    /// <param name="sourceOrigin">The exact effective configuration origin.</param>
    /// <param name="command">The exact configured command string.</param>
    /// <param name="executable">The resolved executable that will receive the command.</param>
    /// <param name="workingDirectory">The exact canonical child working directory.</param>
    /// <param name="usesShell">Whether the command runs through the fixed platform shell.</param>
    /// <param name="exposedData">The bounded descriptions of data supplied to the child.</param>
    internal ExecutableCapabilityRequest(
        GitConfigurationExecutionKind kind,
        string configurationKey,
        GitConfigurationScope sourceScope,
        GitConfigurationOrigin sourceOrigin,
        string command,
        ResolvedExecutable executable,
        CanonicalDirectory workingDirectory,
        bool usesShell,
        ImmutableArray<string> exposedData)
    {
        if (!Enum.IsDefined(kind) || kind == GitConfigurationExecutionKind.None)
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(configurationKey);
        if (configurationKey.Length > MaximumConfigurationKeyCharacters ||
            configurationKey.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The executable configuration key is too long or contains NUL.",
                nameof(configurationKey));
        }

        if (!Enum.IsDefined(sourceScope))
        {
            throw new ArgumentOutOfRangeException(nameof(sourceScope));
        }

        ArgumentNullException.ThrowIfNull(sourceOrigin);
        ArgumentNullException.ThrowIfNull(command);
        if (command.Length == 0 || command.Length > MaximumCommandCharacters ||
            command.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The configured command must be nonempty, bounded, and free of NUL.",
                nameof(command));
        }

        ArgumentNullException.ThrowIfNull(executable);
        ArgumentNullException.ThrowIfNull(workingDirectory);
        if (exposedData.IsDefault || exposedData.Length > MaximumExposureCount ||
            exposedData.Any(static value => string.IsNullOrWhiteSpace(value) ||
                value.Length > MaximumExposureCharacters ||
                value.Contains('\0', StringComparison.Ordinal)) ||
            exposedData.Distinct(StringComparer.Ordinal).Count() != exposedData.Length)
        {
            throw new ArgumentException(
                "Capability data-exposure descriptions must be unique, nonempty, bounded, and free of NUL.",
                nameof(exposedData));
        }

        Kind = kind;
        ConfigurationKey = configurationKey;
        SourceScope = sourceScope;
        SourceOrigin = sourceOrigin;
        Command = command;
        Executable = executable;
        WorkingDirectory = workingDirectory;
        UsesShell = usesShell;
        ExposedData = exposedData;
        CommandHash = ComputeHash();
    }

    /// <summary>
    /// Gets the configured executable behavior family.
    /// </summary>
    internal GitConfigurationExecutionKind Kind { get; }

    /// <summary>
    /// Gets the exact concrete Git configuration key supplying the command.
    /// </summary>
    internal string ConfigurationKey { get; }

    /// <summary>
    /// Gets the effective source scope reported by Git.
    /// </summary>
    internal GitConfigurationScope SourceScope { get; }

    /// <summary>
    /// Gets the exact effective configuration origin reported by Git.
    /// </summary>
    internal GitConfigurationOrigin SourceOrigin { get; }

    /// <summary>
    /// Gets the exact configured command string reviewed by the user.
    /// </summary>
    internal string Command { get; }

    /// <summary>
    /// Gets the resolved executable that will receive the command.
    /// </summary>
    internal ResolvedExecutable Executable { get; }

    /// <summary>
    /// Gets the exact canonical child working directory.
    /// </summary>
    internal CanonicalDirectory WorkingDirectory { get; }

    /// <summary>
    /// Gets whether this invocation uses the fixed platform shell.
    /// </summary>
    internal bool UsesShell { get; }

    /// <summary>
    /// Gets the bounded descriptions of data supplied to the child.
    /// </summary>
    internal ImmutableArray<string> ExposedData { get; }

    /// <summary>
    /// Gets the lowercase SHA-256 hash that invalidates the grant when execution inputs change.
    /// </summary>
    internal string CommandHash { get; }

    private string ComputeHash()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(s_hashDomain);
        AppendInt32(hash, (int)Kind);
        AppendInt32(hash, (int)SourceScope);
        hash.AppendData([UsesShell ? (byte)1 : (byte)0]);
        AppendText(hash, ConfigurationKey);
        AppendBytes(hash, SourceOrigin.GetBytes());
        AppendText(hash, Command);
        AppendText(hash, Executable.Path);
        AppendInt32(hash, (int)Executable.Kind);
        AppendInt64(hash, Executable.Fingerprint.Length);
        AppendInt64(hash, Executable.Fingerprint.LastWriteTimeUtcTicks);
        if (WorkingDirectory.Kind == NativePathKind.UnixBytes)
        {
            hash.AppendData([1]);
            AppendBytes(hash, WorkingDirectory.GetUnixBytes());
        }
        else
        {
            hash.AppendData([2]);
            AppendText(hash, WorkingDirectory.GetWindowsPath());
        }

        foreach (var exposure in ExposedData.Order(StringComparer.Ordinal))
        {
            AppendText(hash, exposure);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void AppendText(IncrementalHash hash, string value)
        => AppendBytes(hash, s_utf8.GetBytes(value));

    private static void AppendBytes(IncrementalHash hash, ReadOnlySpan<byte> value)
    {
        AppendInt32(hash, value.Length);
        hash.AppendData(value);
    }

    private static void AppendInt32(IncrementalHash hash, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static void AppendInt64(IncrementalHash hash, long value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
        hash.AppendData(bytes);
    }
}
