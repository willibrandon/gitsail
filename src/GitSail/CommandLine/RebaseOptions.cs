namespace GitSail.CommandLine;

/// <summary>
/// Contains the typed operands for an interactive rebase workflow.
/// </summary>
/// <param name="Upstream">The upstream revision, or <see langword="null"/> to use the configured upstream.</param>
/// <param name="Onto">The new base revision, or <see langword="null"/> to use the resolved upstream.</param>
internal sealed record RebaseOptions(string? Upstream, string? Onto);
