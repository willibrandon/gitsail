namespace GitSail.Ui;

/// <summary>
/// Classifies whether standard input and output can host an interactive terminal session.
/// </summary>
internal static class TerminalSessionGuard
{
    /// <summary>
    /// Determines whether both terminal directions are attached instead of redirected.
    /// </summary>
    /// <param name="inputRedirected">Whether standard input is redirected.</param>
    /// <param name="outputRedirected">Whether standard output is redirected.</param>
    /// <returns><see langword="true"/> when the terminal UI can safely start.</returns>
    internal static bool IsInteractive(bool inputRedirected, bool outputRedirected)
        => !inputRedirected && !outputRedirected;
}
