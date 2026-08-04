namespace GitSail.Domain;

/// <summary>
/// Identifies the isolated local-path or fixed-script SSH initialization transport.
/// </summary>
internal enum RemoteInitializationKind
{
    /// <summary>
    /// Initializes a bare repository through the local Git executable and an exact platform path.
    /// </summary>
    Local,

    /// <summary>
    /// Initializes a bare repository through an exact SSH destination and fixed POSIX script.
    /// </summary>
    Ssh,
}
