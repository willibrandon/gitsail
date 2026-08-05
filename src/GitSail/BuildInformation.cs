namespace GitSail;

/// <summary>
/// Contains immutable product and build identity values.
/// </summary>
internal static class BuildInformation
{
    /// <summary>
    /// Gets the product name.
    /// </summary>
    internal const string ProductName = "GitSail";

    /// <summary>
    /// Gets the pre-1.0 product version.
    /// </summary>
    internal const string Version = "0.1.0";

    /// <summary>
    /// Gets the human-readable product version.
    /// </summary>
    internal static string DisplayVersion => $"{ProductName} {Version}";
}
