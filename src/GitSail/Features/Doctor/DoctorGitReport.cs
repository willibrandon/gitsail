namespace GitSail.Features.Doctor;

/// <summary>
/// Contains trusted Git resolution, version, baseline compatibility, and failure details.
/// </summary>
/// <param name="Available">Whether Git resolved and returned a valid version.</param>
/// <param name="Path">The canonical Git executable path, when available.</param>
/// <param name="Version">The parsed Git version, when available.</param>
/// <param name="MeetsMinimumVersion">Whether Git meets the documented 2.36 baseline.</param>
/// <param name="Error">The resolution or version error, when unavailable.</param>
internal sealed record DoctorGitReport(
    bool Available,
    string? Path,
    string? Version,
    bool MeetsMinimumVersion,
    string? Error);
