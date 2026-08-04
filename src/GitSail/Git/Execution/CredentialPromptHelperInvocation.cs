using System.Globalization;

namespace GitSail.Git.Execution;

/// <summary>
/// Validates the private environment and Git-supplied argument for one helper process.
/// </summary>
internal sealed class CredentialPromptHelperInvocation : IDisposable
{
    private byte[]? _nonce;

    private CredentialPromptHelperInvocation(
        string prompt,
        CredentialPromptKind kind,
        string? endpoint,
        string? sessionId,
        byte[]? nonce,
        int? parentProcessId)
    {
        Prompt = prompt;
        Kind = kind;
        Endpoint = endpoint;
        SessionId = sessionId;
        _nonce = nonce;
        ParentProcessId = parentProcessId;
    }

    /// <summary>
    /// Gets the exact Git- or SSH-supplied prompt text.
    /// </summary>
    internal string Prompt { get; }

    /// <summary>
    /// Gets the classified response treatment for terminal fallback.
    /// </summary>
    internal CredentialPromptKind Kind { get; }

    /// <summary>
    /// Gets the operation pipe name when a parent endpoint was supplied.
    /// </summary>
    internal string? Endpoint { get; }

    /// <summary>
    /// Gets the exact operation session identity when a parent endpoint was supplied.
    /// </summary>
    internal string? SessionId { get; }

    /// <summary>
    /// Gets the declared parent process identity when a parent endpoint was supplied.
    /// </summary>
    internal int? ParentProcessId { get; }

    /// <summary>
    /// Gets whether every field required for authenticated parent communication is valid.
    /// </summary>
    internal bool HasAuthenticatedParent =>
        !string.IsNullOrWhiteSpace(Endpoint) &&
        !string.IsNullOrWhiteSpace(SessionId) &&
        _nonce is not null &&
        ParentProcessId > 0;

    /// <summary>
    /// Gets a read-only view of the operation nonce while this invocation remains undisposed.
    /// </summary>
    internal ReadOnlyMemory<byte> Nonce
        => _nonce ?? throw new ObjectDisposedException(nameof(CredentialPromptHelperInvocation));

    /// <summary>
    /// Detects the private environment marker without interpreting user-facing commands.
    /// </summary>
    /// <param name="environment">The explicit current-process environment source.</param>
    /// <returns><see langword="true"/> only for the private askpass protocol marker.</returns>
    internal static bool IsRequested(IProcessEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return string.Equals(
                environment.GetVariable(CredentialPromptProtocol.ProtocolVariable),
                CredentialPromptProtocol.ProtocolVersion,
                StringComparison.Ordinal) &&
            string.Equals(
                environment.GetVariable(CredentialPromptProtocol.KindVariable),
                CredentialPromptProtocol.HelperKind,
                StringComparison.Ordinal);
    }

    /// <summary>
    /// Creates one helper invocation from exact private variables and one Git prompt argument.
    /// </summary>
    /// <param name="arguments">The arguments supplied by Git or SSH.</param>
    /// <param name="environment">The explicit current-process environment source.</param>
    /// <returns>The validated invocation.</returns>
    internal static CredentialPromptHelperInvocation Create(
        IReadOnlyList<string> arguments,
        IProcessEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(environment);
        if (!IsRequested(environment) || arguments.Count != 1 ||
            string.IsNullOrWhiteSpace(arguments[0]) ||
            arguments[0].Contains('\0', StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The private credential helper requires one bounded nonempty prompt argument.");
        }

        if (System.Text.Encoding.UTF8.GetByteCount(arguments[0]) >
            CredentialPromptProtocol.MaximumTextBytes)
        {
            throw new InvalidDataException("The private credential helper prompt is too large.");
        }

        var endpoint = environment.GetVariable(CredentialPromptProtocol.EndpointVariable);
        var sessionId = environment.GetVariable(CredentialPromptProtocol.SessionVariable);
        var nonce = TryDecodeNonce(environment.GetVariable(CredentialPromptProtocol.NonceVariable));
        var parentProcessText = environment.GetVariable(CredentialPromptProtocol.ParentProcessVariable);
        int? parentProcessId = int.TryParse(
            parentProcessText,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var parsedParentProcessId) && parsedParentProcessId > 0
                ? parsedParentProcessId
                : null;
        return new CredentialPromptHelperInvocation(
            arguments[0],
            CredentialPromptClassifier.Classify(arguments[0]),
            endpoint,
            sessionId,
            nonce,
            parentProcessId);
    }

    /// <summary>
    /// Clears the operation nonce retained for the short helper process lifetime.
    /// </summary>
    public void Dispose()
    {
        if (_nonce is null)
        {
            return;
        }

        System.Security.Cryptography.CryptographicOperations.ZeroMemory(_nonce);
        _nonce = null;
    }

    private static byte[]? TryDecodeNonce(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Any(static character =>
            !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
        {
            return null;
        }

        var padding = (value.Length % 4) switch
        {
            0 => string.Empty,
            2 => "==",
            3 => "=",
            _ => null,
        };
        if (padding is null)
        {
            return null;
        }

        try
        {
            var bytes = Convert.FromBase64String(
                value.Replace('-', '+').Replace('_', '/') + padding);
            if (bytes.Length == 32)
            {
                return bytes;
            }

            System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
