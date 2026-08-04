using GitSail.Git.Execution;

namespace GitSail.ServiceTests;

/// <summary>
/// Cancels unexpected credential prompts in isolated service tests.
/// </summary>
internal sealed class TestCredentialPromptResponder : ICredentialPromptResponder
{
    /// <summary>
    /// Cancels an unexpected test prompt without retaining any response data.
    /// </summary>
    /// <param name="operation">The isolated test operation label.</param>
    /// <param name="prompt">The bounded test prompt text.</param>
    /// <param name="kind">The classified test prompt treatment.</param>
    /// <param name="cancellationToken">Signals test cancellation.</param>
    /// <returns>A cancelled helper response.</returns>
    public Task<byte[]?> RequestAsync(
        string operation,
        string prompt,
        CredentialPromptKind kind,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<byte[]?>(null);
    }
}
