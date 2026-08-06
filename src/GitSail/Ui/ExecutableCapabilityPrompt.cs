using GitSail.Git.Execution;

namespace GitSail.Ui;

/// <summary>
/// Identifies one serialized executable-capability review waiting for a user decision.
/// </summary>
internal sealed class ExecutableCapabilityPrompt
{
    /// <summary>
    /// Initializes one stable in-process prompt around an exact capability request.
    /// </summary>
    /// <param name="id">The monotonically increasing prompt identity.</param>
    /// <param name="request">The exact executable capability review.</param>
    internal ExecutableCapabilityPrompt(long id, ExecutableCapabilityRequest request)
    {
        if (id <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(id));
        }

        ArgumentNullException.ThrowIfNull(request);
        Id = id;
        Request = request;
    }

    /// <summary>
    /// Gets the stable in-process identity used to reconcile the review window.
    /// </summary>
    internal long Id { get; }

    /// <summary>
    /// Gets the exact command, source, executable, directory, and data exposure review.
    /// </summary>
    internal ExecutableCapabilityRequest Request { get; }
}
