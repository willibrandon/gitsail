namespace GitSail.Features.Doctor;

/// <summary>
/// Contains one version-gated application capability and its availability.
/// </summary>
/// <param name="Name">The stable capability name.</param>
/// <param name="Available">Whether the resolved dependency version supports the capability.</param>
/// <param name="Requirement">The documented minimum version for the capability.</param>
internal sealed record DoctorCapabilityReport(
    string Name,
    bool Available,
    string Requirement);
