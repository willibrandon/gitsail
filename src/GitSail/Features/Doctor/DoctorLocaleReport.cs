namespace GitSail.Features.Doctor;

/// <summary>
/// Contains the process cultures and console encodings used for presentation.
/// </summary>
/// <param name="Culture">The current formatting culture.</param>
/// <param name="UICulture">The current resource culture.</param>
/// <param name="InputEncoding">The standard-input encoding.</param>
/// <param name="OutputEncoding">The standard-output encoding.</param>
/// <param name="Globalization">The available culture-data provider classification.</param>
internal sealed record DoctorLocaleReport(
    string Culture,
    string UICulture,
    string InputEncoding,
    string OutputEncoding,
    string Globalization);
