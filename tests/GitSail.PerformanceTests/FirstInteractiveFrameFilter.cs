using System.Text;
using Hex1b;
using Hex1b.Tokens;

namespace GitSail.PerformanceTests;

/// <summary>
/// Captures when the first complete GitSail frame reaches the terminal presentation boundary.
/// </summary>
internal sealed class FirstInteractiveFrameFilter : IHex1bTerminalPresentationFilter
{
    private readonly StringBuilder _visibleText = new();
    private readonly TaskCompletionSource<TimeSpan> _firstFrame =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Gets the elapsed session time at which the first complete GitSail frame was presented.
    /// </summary>
    internal Task<TimeSpan> FirstFrame => _firstFrame.Task;

    ValueTask IHex1bTerminalPresentationFilter.OnSessionStartAsync(
        int width,
        int height,
        DateTimeOffset timestamp,
        CancellationToken ct)
        => ValueTask.CompletedTask;

    ValueTask<IReadOnlyList<AnsiToken>> IHex1bTerminalPresentationFilter.OnOutputAsync(
        IReadOnlyList<AppliedToken> appliedTokens,
        TimeSpan elapsed,
        CancellationToken ct)
    {
        foreach (var appliedToken in appliedTokens)
        {
            if (appliedToken.Token is TextToken textToken)
            {
                _visibleText.Append(textToken.Text);
            }
        }

        var visibleText = _visibleText.ToString();
        if (visibleText.Contains("GitSail", StringComparison.Ordinal) &&
            visibleText.Contains("Unstaged", StringComparison.Ordinal) &&
            visibleText.Contains("Staged", StringComparison.Ordinal) &&
            visibleText.Contains("Diff", StringComparison.Ordinal) &&
            visibleText.Contains("Commit message", StringComparison.Ordinal) &&
            visibleText.Contains("Quit", StringComparison.Ordinal))
        {
            _firstFrame.TrySetResult(elapsed);
        }

        return ValueTask.FromResult<IReadOnlyList<AnsiToken>>(
            appliedTokens.Select(appliedToken => appliedToken.Token).ToArray());
    }

    ValueTask IHex1bTerminalPresentationFilter.OnInputAsync(
        IReadOnlyList<AnsiToken> tokens,
        TimeSpan elapsed,
        CancellationToken ct)
        => ValueTask.CompletedTask;

    ValueTask IHex1bTerminalPresentationFilter.OnResizeAsync(
        int width,
        int height,
        TimeSpan elapsed,
        CancellationToken ct)
        => ValueTask.CompletedTask;

    ValueTask IHex1bTerminalPresentationFilter.OnSessionEndAsync(
        TimeSpan elapsed,
        CancellationToken ct)
        => ValueTask.CompletedTask;
}
