namespace GitSail.Features.Doctor;

/// <summary>
/// Contains one resolved application-owned directory and its non-mutating status.
/// </summary>
/// <param name="Name">The stable directory purpose.</param>
/// <param name="Path">The fully qualified directory path, when resolution succeeded.</param>
/// <param name="Status">The existence, type, reparse, and Unix-mode status.</param>
internal sealed record DoctorPathReport(
    string Name,
    string? Path,
    string Status);
