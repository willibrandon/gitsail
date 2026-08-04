namespace GitSail.CommandLine;

/// <summary>
/// Contains the optional explicit output path for one requested application trace.
/// </summary>
/// <param name="OutputFile">The explicit trace file, or <see langword="null"/> for a generated user-state path.</param>
internal sealed record TraceOptions(string? OutputFile);
