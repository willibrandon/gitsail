namespace GitSail.Domain;

/// <summary>
/// Identifies one explicitly supported OpenSSH key algorithm and strength.
/// </summary>
internal enum SshKeyAlgorithm
{
    /// <summary>
    /// Creates a modern Ed25519 key with OpenSSH's fixed algorithm parameters.
    /// </summary>
    Ed25519,

    /// <summary>
    /// Creates an RSA key with a 4,096-bit modulus.
    /// </summary>
    Rsa4096,

    /// <summary>
    /// Creates an ECDSA key on the 521-bit NIST curve.
    /// </summary>
    Ecdsa521,
}
