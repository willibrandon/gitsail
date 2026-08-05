namespace GitSail.Ui;

/// <summary>
/// Tracks the root viewport and fits popup dimensions inside a one-cell margin.
/// </summary>
internal sealed class PopupViewport
{
    private int _width;
    private int _height;

    /// <summary>
    /// Records the latest root layout dimensions and selects the responsive branch.
    /// </summary>
    /// <param name="width">The available root width in terminal cells.</param>
    /// <param name="height">The available root height in terminal cells.</param>
    /// <returns><see langword="true"/> so the associated responsive branch remains active.</returns>
    internal bool Capture(int width, int height)
    {
        _width = width;
        _height = height;
        return true;
    }

    /// <summary>
    /// Fits a preferred popup width within the recorded viewport.
    /// </summary>
    /// <param name="preferredWidth">The preferred popup width in terminal cells.</param>
    /// <returns>The preferred width or the largest width that retains a one-cell margin.</returns>
    internal int FitWidth(int preferredWidth)
        => _width <= 0
            ? preferredWidth
            : Math.Min(preferredWidth, Math.Max(1, _width - 2));

    /// <summary>
    /// Fits a preferred popup height within the recorded viewport.
    /// </summary>
    /// <param name="preferredHeight">The preferred popup height in terminal cells.</param>
    /// <returns>The preferred height or the largest height that retains a one-cell margin.</returns>
    internal int FitHeight(int preferredHeight)
        => _height <= 0
            ? preferredHeight
            : Math.Min(preferredHeight, Math.Max(1, _height - 2));
}
