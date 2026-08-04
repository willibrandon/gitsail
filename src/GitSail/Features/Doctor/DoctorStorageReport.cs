namespace GitSail.Features.Doctor;

/// <summary>
/// Contains platform configuration, cache, state, and trace directory diagnostics.
/// </summary>
/// <param name="Configuration">The application configuration directory.</param>
/// <param name="Cache">The application cache directory.</param>
/// <param name="State">The application state directory.</param>
/// <param name="Traces">The generated trace directory.</param>
/// <param name="Error">The user-directory resolution error, when present.</param>
internal sealed record DoctorStorageReport(
    DoctorPathReport Configuration,
    DoctorPathReport Cache,
    DoctorPathReport State,
    DoctorPathReport Traces,
    string? Error);
