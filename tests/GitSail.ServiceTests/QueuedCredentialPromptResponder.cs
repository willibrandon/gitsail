using GitSail.Git.Execution;

namespace GitSail.ServiceTests;

/// <summary>
/// Returns deterministic owned responses in order for black-box Git helper tests.
/// </summary>
internal sealed class QueuedCredentialPromptResponder : ICredentialPromptResponder
{
    private readonly Queue<byte[]> _responses;
    private readonly Lock _lock = new();

    /// <summary>
    /// Initializes the queue from UTF-8 response text values.
    /// </summary>
    /// <param name="responses">The ordered responses copied into owned byte arrays.</param>
    internal QueuedCredentialPromptResponder(params string[] responses)
    {
        ArgumentNullException.ThrowIfNull(responses);
        _responses = new Queue<byte[]>(responses.Select(System.Text.Encoding.UTF8.GetBytes));
    }

    /// <summary>
    /// Gets the ordered prompt treatments observed from black-box Git.
    /// </summary>
    internal List<CredentialPromptKind> Kinds { get; } = [];

    /// <summary>
    /// Returns the next owned response and records the classified prompt treatment.
    /// </summary>
    /// <param name="operation">The black-box Git operation label.</param>
    /// <param name="prompt">The bounded black-box Git prompt.</param>
    /// <param name="kind">The classified response treatment.</param>
    /// <param name="cancellationToken">Signals test cancellation.</param>
    /// <returns>The next owned response.</returns>
    public Task<byte[]?> RequestAsync(
        string operation,
        string prompt,
        CredentialPromptKind kind,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            if (_responses.Count == 0)
            {
                throw new InvalidOperationException("Black-box Git requested more credential responses than expected.");
            }

            Kinds.Add(kind);
            return Task.FromResult<byte[]?>(_responses.Dequeue());
        }
    }
}
