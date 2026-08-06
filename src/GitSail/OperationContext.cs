using GitSail.Domain;

namespace GitSail;

/// <summary>
/// Supplies identity, cancellation, and progress publication to one supervised operation.
/// </summary>
internal sealed class OperationContext
{
    private readonly Action<string?, double?> _report;

    /// <summary>
    /// Initializes the execution context for one accepted operation.
    /// </summary>
    /// <param name="id">The stable operation identifier.</param>
    /// <param name="cancellationToken">Signals operation cancellation or supervisor shutdown.</param>
    /// <param name="report">Publishes validated progress updates.</param>
    internal OperationContext(
        OperationId id,
        CancellationToken cancellationToken,
        Action<string?, double?> report)
    {
        ArgumentNullException.ThrowIfNull(report);
        Id = id;
        CancellationToken = cancellationToken;
        _report = report;
    }

    /// <summary>
    /// Gets the stable operation identifier.
    /// </summary>
    internal OperationId Id { get; }

    /// <summary>
    /// Gets the token that signals operation cancellation or supervisor shutdown.
    /// </summary>
    internal CancellationToken CancellationToken { get; }

    /// <summary>
    /// Publishes the newest control-safe progress detail and optional completion fraction.
    /// </summary>
    /// <param name="detail">The optional control-safe progress detail.</param>
    /// <param name="progress">The optional completion fraction from zero through one.</param>
    internal void Report(string? detail = null, double? progress = null)
    {
        if (progress is < 0 or > 1 || double.IsNaN(progress.GetValueOrDefault()))
        {
            throw new ArgumentOutOfRangeException(nameof(progress));
        }

        _report(detail, progress);
    }
}
