using System.Text;
using Hex1b;
using Hex1b.Tokens;

namespace GitSail.PerformanceTests;

/// <summary>
/// Captures when the first complete GitSail frame reaches the terminal presentation boundary.
/// </summary>
internal sealed class FirstInteractiveFrameFilter : IHex1bTerminalPresentationFilter
{
    private readonly StringBuilder _fallbackText = new();
    private readonly TaskCompletionSource<TimeSpan> _firstFrame =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string[] _cells = [];
    private int _width;
    private int _height;

    /// <summary>
    /// Gets the elapsed session time at which the first complete GitSail frame was presented.
    /// </summary>
    internal Task<TimeSpan> FirstFrame => _firstFrame.Task;

    ValueTask IHex1bTerminalPresentationFilter.OnSessionStartAsync(
        int width,
        int height,
        DateTimeOffset timestamp,
        CancellationToken ct)
    {
        ResetSurface(width, height);
        return ValueTask.CompletedTask;
    }

    ValueTask<IReadOnlyList<AnsiToken>> IHex1bTerminalPresentationFilter.OnOutputAsync(
        IReadOnlyList<AppliedToken> appliedTokens,
        TimeSpan elapsed,
        CancellationToken ct)
    {
        foreach (var appliedToken in appliedTokens)
        {
            foreach (var impact in appliedToken.CellImpacts)
            {
                if (impact.X >= 0 && impact.X < _width &&
                    impact.Y >= 0 && impact.Y < _height)
                {
                    _cells[(impact.Y * _width) + impact.X] = impact.Cell.Character;
                }
            }

            if (appliedToken.Token is TextToken textToken)
            {
                _fallbackText.Append(textToken.Text);
            }
        }

        if (IsCompleteWorkspace(CreateSurfaceText()) ||
            IsCompleteWorkspace(_fallbackText.ToString()))
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
    {
        ResetSurface(width, height);
        return ValueTask.CompletedTask;
    }

    ValueTask IHex1bTerminalPresentationFilter.OnSessionEndAsync(
        TimeSpan elapsed,
        CancellationToken ct)
        => ValueTask.CompletedTask;

    private void ResetSurface(int width, int height)
    {
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height));
        }

        _width = width;
        _height = height;
        _cells = [.. Enumerable.Repeat(" ", checked(width * height))];
    }

    private string CreateSurfaceText()
    {
        if (_cells.Length == 0)
        {
            return string.Empty;
        }

        var text = new StringBuilder(_cells.Sum(static value => value.Length) + _height);
        for (var row = 0; row < _height; row++)
        {
            for (var column = 0; column < _width; column++)
            {
                text.Append(_cells[(row * _width) + column]);
            }

            text.Append('\n');
        }

        return text.ToString();
    }

    private static bool IsCompleteWorkspace(string visibleText)
        => visibleText.Contains("GitSail", StringComparison.Ordinal) &&
            visibleText.Contains("Unstaged", StringComparison.Ordinal) &&
            visibleText.Contains("Staged", StringComparison.Ordinal) &&
            visibleText.Contains("Diff", StringComparison.Ordinal) &&
            visibleText.Contains("Commit message", StringComparison.Ordinal) &&
            visibleText.Contains("Quit", StringComparison.Ordinal);
}
