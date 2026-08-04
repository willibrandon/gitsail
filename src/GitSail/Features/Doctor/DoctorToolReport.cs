namespace GitSail.Features.Doctor;

/// <summary>
/// Contains trusted resolution diagnostics for one optional executable tool.
/// </summary>
/// <param name="Name">The stable tool name.</param>
/// <param name="Available">Whether the executable resolved safely.</param>
/// <param name="Path">The canonical executable path, when available.</param>
/// <param name="Error">The resolution error, when unavailable.</param>
internal sealed record DoctorToolReport(
    string Name,
    bool Available,
    string? Path,
    string? Error);
