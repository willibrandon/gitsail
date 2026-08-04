namespace GitSail.Features.Doctor;

/// <summary>
/// Contains read-only repository discovery, storage-format, and trust diagnostics.
/// </summary>
/// <param name="Available">Whether Git discovered a repository.</param>
/// <param name="WorkTree">The canonical worktree path, when present.</param>
/// <param name="GitDirectory">The canonical per-worktree Git directory, when available.</param>
/// <param name="IsBare">Whether the discovered repository is bare.</param>
/// <param name="ObjectFormat">The repository object format, when available.</param>
/// <param name="Trust">The Git-derived repository trust classification.</param>
/// <param name="Error">The sanitized discovery error, when unavailable.</param>
internal sealed record DoctorRepositoryReport(
    bool Available,
    string? WorkTree,
    string? GitDirectory,
    bool? IsBare,
    string? ObjectFormat,
    string Trust,
    string? Error);
