namespace GitSail.Domain;

/// <summary>
/// Describes one reviewed terminal-attached OpenSSH key-generation request.
/// </summary>
/// <param name="Algorithm">The explicitly selected key algorithm and strength.</param>
/// <param name="FilePath">The fully qualified private-key output path.</param>
/// <param name="Comment">The bounded public-key comment.</param>
/// <param name="ReplaceExisting">Whether existing private or public output was explicitly confirmed.</param>
internal sealed record SshKeyCreationRequest(
    SshKeyAlgorithm Algorithm,
    string FilePath,
    string Comment,
    bool ReplaceExisting);
