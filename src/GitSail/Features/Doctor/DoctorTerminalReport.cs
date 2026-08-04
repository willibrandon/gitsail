namespace GitSail.Features.Doctor;

/// <summary>
/// Contains terminal attachment, dimensions, encoding, color, and pointer diagnostics.
/// </summary>
/// <param name="Description">The concise terminal attachment or dimension description.</param>
/// <param name="InputRedirected">Whether standard input is redirected.</param>
/// <param name="OutputRedirected">Whether standard output is redirected.</param>
/// <param name="Width">The attached terminal width, when available.</param>
/// <param name="Height">The attached terminal height, when available.</param>
/// <param name="Color">The conservative color capability classification.</param>
/// <param name="Input">The terminal input capability classification.</param>
/// <param name="Mouse">The application pointer-input status.</param>
/// <param name="Unicode">The console output encoding.</param>
/// <param name="Clipboard">The terminal clipboard mechanism and probe status.</param>
internal sealed record DoctorTerminalReport(
    string Description,
    bool InputRedirected,
    bool OutputRedirected,
    int? Width,
    int? Height,
    string Color,
    string Input,
    string Mouse,
    string Unicode,
    string Clipboard);
