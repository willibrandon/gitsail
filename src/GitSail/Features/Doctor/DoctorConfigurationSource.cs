namespace GitSail.Features.Doctor;

/// <summary>
/// Contains one distinct Git configuration scope and origin without its keys or values.
/// </summary>
/// <param name="Scope">The precedence scope reported by Git.</param>
/// <param name="Origin">The control-safe source origin reported by Git.</param>
internal sealed record DoctorConfigurationSource(
    string Scope,
    string Origin);
