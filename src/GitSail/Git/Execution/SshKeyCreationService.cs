using GitSail.Domain;
using System.Collections.Immutable;

namespace GitSail.Git.Execution;

/// <summary>
/// Validates and runs reviewed OpenSSH key creation with the real terminal attached.
/// </summary>
internal sealed class SshKeyCreationService
{
    private const int MaximumCommentCharacters = 1024;
    private const int MaximumPathCharacters = 32_767;
    private readonly ResolvedExecutable _sshKeygen;
    private readonly ITerminalChildProcessRunner _terminalRunner;
    private readonly GitChildEnvironmentFactory _environmentFactory;
    private readonly IProcessEnvironment _processEnvironment;

    /// <summary>
    /// Initializes SSH key creation over a resolved executable and terminal-attached process boundary.
    /// </summary>
    /// <param name="sshKeygen">The resolved and fingerprinted OpenSSH key generator.</param>
    /// <param name="terminalRunner">The terminal-attached child-process runner.</param>
    /// <param name="environmentFactory">The classified child-environment factory.</param>
    /// <param name="processEnvironment">The classified startup environment used for the default key directory.</param>
    internal SshKeyCreationService(
        ResolvedExecutable sshKeygen,
        ITerminalChildProcessRunner terminalRunner,
        GitChildEnvironmentFactory environmentFactory,
        IProcessEnvironment processEnvironment)
    {
        ArgumentNullException.ThrowIfNull(sshKeygen);
        ArgumentNullException.ThrowIfNull(terminalRunner);
        ArgumentNullException.ThrowIfNull(environmentFactory);
        ArgumentNullException.ThrowIfNull(processEnvironment);
        if (sshKeygen.Kind != ProgramKind.SshKeygen)
        {
            throw new ArgumentException(
                "SSH key creation requires the resolved ssh-keygen executable.",
                nameof(sshKeygen));
        }

        _sshKeygen = sshKeygen;
        _terminalRunner = terminalRunner;
        _environmentFactory = environmentFactory;
        _processEnvironment = processEnvironment;
    }

    /// <summary>
    /// Runs one reviewed request after revalidating its path and replacement decision.
    /// </summary>
    /// <param name="workingDirectory">The existing directory inherited by the terminal child.</param>
    /// <param name="request">The exact reviewed algorithm, path, comment, and replacement decision.</param>
    /// <param name="cancellationToken">Signals terminal-child cancellation and reaping.</param>
    /// <returns>The normalized <c>ssh-keygen</c> exit status.</returns>
    internal async Task<int> RunAsync(
        CanonicalDirectory workingDirectory,
        SshKeyCreationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);
        ArgumentNullException.ThrowIfNull(request);
        if (!TryValidateRequest(
                request.Algorithm,
                request.FilePath,
                request.Comment,
                request.ReplaceExisting,
                out var validated,
                out var error))
        {
            throw new InvalidDataException(error);
        }

        EnsureParentDirectory(validated.FilePath);
        if (RequiresReplacement(validated.FilePath) && !validated.ReplaceExisting)
        {
            throw new InvalidOperationException(
                "The private-key or public-key output already exists and replacement was not confirmed.");
        }

        var invocation = new ProcessInvocation(
            _sshKeygen,
            CreateArguments(validated),
            workingDirectory,
            _environmentFactory.CreateToolEnvironment(),
            StandardInputSource.Empty(),
            OutputPolicy.Create(1, 1));
        return await _terminalRunner.RunAsync(invocation, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Validates and normalizes one user-entered SSH key request without changing the file system.
    /// </summary>
    /// <param name="algorithm">The explicitly selected algorithm and strength.</param>
    /// <param name="filePath">The requested fully qualified private-key output path.</param>
    /// <param name="comment">The requested public-key comment.</param>
    /// <param name="replaceExisting">Whether existing output was explicitly confirmed.</param>
    /// <param name="request">The normalized request when validation succeeds.</param>
    /// <param name="error">The actionable validation error when validation fails.</param>
    /// <returns><see langword="true"/> when the request is safe to review.</returns>
    internal static bool TryValidateRequest(
        SshKeyAlgorithm algorithm,
        string filePath,
        string comment,
        bool replaceExisting,
        out SshKeyCreationRequest request,
        out string? error)
    {
        request = new SshKeyCreationRequest(algorithm, string.Empty, string.Empty, replaceExisting);
        error = null;
        if (!Enum.IsDefined(algorithm))
        {
            error = "Select a supported SSH key algorithm.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(filePath))
        {
            error = "Enter a fully qualified private-key output path.";
            return false;
        }

        if (filePath.Length > MaximumPathCharacters || filePath.Contains('\0', StringComparison.Ordinal))
        {
            error = "The private-key output path is invalid or exceeds the supported length.";
            return false;
        }

        string normalizedPath;
        try
        {
            if (!Path.IsPathFullyQualified(filePath))
            {
                error = "The private-key output path must be fully qualified.";
                return false;
            }

            normalizedPath = Path.GetFullPath(filePath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = $"The private-key output path is invalid: {exception.Message}";
            return false;
        }

        if (Directory.Exists(normalizedPath) || Directory.Exists($"{normalizedPath}.pub"))
        {
            error = "The private-key or public-key output path names a directory.";
            return false;
        }

        if (comment.Length > MaximumCommentCharacters ||
            comment.Contains('\0', StringComparison.Ordinal) ||
            comment.Contains('\r', StringComparison.Ordinal) ||
            comment.Contains('\n', StringComparison.Ordinal))
        {
            error = "The public-key comment must be one line of at most 1,024 characters.";
            return false;
        }

        request = new SshKeyCreationRequest(
            algorithm,
            normalizedPath,
            comment,
            replaceExisting);
        return true;
    }

    /// <summary>
    /// Gets the platform-user SSH key path for the selected algorithm.
    /// </summary>
    /// <param name="environment">The classified process environment containing the user profile.</param>
    /// <param name="algorithm">The selected key algorithm.</param>
    /// <returns>The fully qualified default private-key path.</returns>
    internal static string GetDefaultKeyPath(
        IProcessEnvironment environment,
        SshKeyAlgorithm algorithm)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var home = environment.IsWindows
            ? environment.GetVariable("USERPROFILE")
            : environment.GetVariable("HOME");
        if (string.IsNullOrWhiteSpace(home) || !Path.IsPathFullyQualified(home))
        {
            throw new InvalidOperationException(
                environment.IsWindows
                    ? "USERPROFILE is required to select the default SSH key path."
                    : "HOME is required to select the default SSH key path.");
        }

        return Path.GetFullPath(Path.Combine(home, ".ssh", GetDefaultFileName(algorithm)));
    }

    /// <summary>
    /// Determines whether either output path already exists and requires explicit replacement review.
    /// </summary>
    /// <param name="filePath">The fully qualified private-key path.</param>
    /// <returns><see langword="true"/> when the private or public output already exists.</returns>
    internal static bool RequiresReplacement(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        return File.Exists(filePath) || File.Exists($"{filePath}.pub");
    }

    /// <summary>
    /// Gets the concise user-facing name of one supported key choice.
    /// </summary>
    /// <param name="algorithm">The selected key algorithm.</param>
    /// <returns>The algorithm and strength label.</returns>
    internal static string GetDisplayName(SshKeyAlgorithm algorithm)
        => algorithm switch
        {
            SshKeyAlgorithm.Ed25519 => "Ed25519",
            SshKeyAlgorithm.Rsa4096 => "RSA 4096",
            SshKeyAlgorithm.Ecdsa521 => "ECDSA 521",
            _ => throw new ArgumentOutOfRangeException(nameof(algorithm)),
        };

    /// <summary>
    /// Gets the next supported key choice in the stable UI cycle.
    /// </summary>
    /// <param name="algorithm">The current key algorithm.</param>
    /// <returns>The next algorithm, wrapping to Ed25519.</returns>
    internal static SshKeyAlgorithm GetNextAlgorithm(SshKeyAlgorithm algorithm)
        => algorithm switch
        {
            SshKeyAlgorithm.Ed25519 => SshKeyAlgorithm.Rsa4096,
            SshKeyAlgorithm.Rsa4096 => SshKeyAlgorithm.Ecdsa521,
            SshKeyAlgorithm.Ecdsa521 => SshKeyAlgorithm.Ed25519,
            _ => throw new ArgumentOutOfRangeException(nameof(algorithm)),
        };

    /// <summary>
    /// Gets the conventional OpenSSH private-key file name for one supported algorithm.
    /// </summary>
    /// <param name="algorithm">The selected key algorithm.</param>
    /// <returns>The conventional private-key file name.</returns>
    internal static string GetDefaultFileName(SshKeyAlgorithm algorithm)
        => algorithm switch
        {
            SshKeyAlgorithm.Ed25519 => "id_ed25519",
            SshKeyAlgorithm.Rsa4096 => "id_rsa",
            SshKeyAlgorithm.Ecdsa521 => "id_ecdsa",
            _ => throw new ArgumentOutOfRangeException(nameof(algorithm)),
        };

    private static ImmutableArray<ProcessArgument> CreateArguments(SshKeyCreationRequest request)
    {
        var arguments = new List<ProcessArgument>
        {
            ProcessArgument.Literal("-t"),
            ProcessArgument.Literal(request.Algorithm switch
            {
                SshKeyAlgorithm.Ed25519 => "ed25519",
                SshKeyAlgorithm.Rsa4096 => "rsa",
                SshKeyAlgorithm.Ecdsa521 => "ecdsa",
                _ => throw new ArgumentOutOfRangeException(nameof(request)),
            }),
        };
        if (request.Algorithm is SshKeyAlgorithm.Rsa4096 or SshKeyAlgorithm.Ecdsa521)
        {
            arguments.Add(ProcessArgument.Literal("-b"));
            arguments.Add(ProcessArgument.Literal(
                request.Algorithm == SshKeyAlgorithm.Rsa4096 ? "4096" : "521"));
        }

        arguments.Add(ProcessArgument.Literal("-a"));
        arguments.Add(ProcessArgument.Literal("100"));
        arguments.Add(ProcessArgument.Literal("-f"));
        arguments.Add(ProcessArgument.Literal(request.FilePath));
        if (request.Comment.Length > 0)
        {
            arguments.Add(ProcessArgument.Literal("-C"));
            arguments.Add(ProcessArgument.Literal(request.Comment));
        }

        return [.. arguments];
    }

    private void EnsureParentDirectory(string filePath)
    {
        var parent = Path.GetDirectoryName(filePath)
            ?? throw new InvalidDataException("The private-key output path has no parent directory.");
        if (Directory.Exists(parent))
        {
            return;
        }

        var defaultParent = Path.GetDirectoryName(
            GetDefaultKeyPath(_processEnvironment, SshKeyAlgorithm.Ed25519));
        if (!string.Equals(
                Path.GetFullPath(parent),
                Path.GetFullPath(defaultParent!),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new DirectoryNotFoundException(
                "The selected key directory does not exist. Create it before generating the key.");
        }

        if (OperatingSystem.IsWindows())
        {
            _ = Directory.CreateDirectory(parent);
        }
        else
        {
            const UnixFileMode mode = UnixFileMode.UserRead |
                UnixFileMode.UserWrite |
                UnixFileMode.UserExecute;
            _ = Directory.CreateDirectory(parent, mode);
        }
    }
}
