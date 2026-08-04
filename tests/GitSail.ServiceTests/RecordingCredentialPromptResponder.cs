using GitSail.Git.Execution;

namespace GitSail.ServiceTests;

/// <summary>
/// Records one authenticated helper request and returns deterministic owned response bytes.
/// </summary>
internal sealed class RecordingCredentialPromptResponder : ICredentialPromptResponder
{
    private readonly byte[] _response;

    /// <summary>
    /// Initializes a responder with one deterministic UTF-8 response.
    /// </summary>
    /// <param name="response">The response copied for each request.</param>
    internal RecordingCredentialPromptResponder(ReadOnlySpan<byte> response)
    {
        _response = response.ToArray();
    }

    /// <summary>
    /// Gets the operation label received from the authenticated parent endpoint.
    /// </summary>
    internal string? Operation { get; private set; }

    /// <summary>
    /// Gets the prompt text received from the authenticated parent endpoint.
    /// </summary>
    internal string? Prompt { get; private set; }

    /// <summary>
    /// Gets the response treatment classified by the authenticated parent endpoint.
    /// </summary>
    internal CredentialPromptKind? Kind { get; private set; }

    /// <summary>
    /// Records one request and returns a fresh owned copy of the deterministic response.
    /// </summary>
    /// <param name="operation">The test operation label.</param>
    /// <param name="prompt">The bounded test prompt.</param>
    /// <param name="kind">The classified response treatment.</param>
    /// <param name="cancellationToken">Signals test cancellation.</param>
    /// <returns>A fresh owned response copy.</returns>
    public Task<byte[]?> RequestAsync(
        string operation,
        string prompt,
        CredentialPromptKind kind,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Operation = operation;
        Prompt = prompt;
        Kind = kind;
        return Task.FromResult<byte[]?>([.. _response]);
    }
}
