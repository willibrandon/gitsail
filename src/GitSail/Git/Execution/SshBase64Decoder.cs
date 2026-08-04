namespace GitSail.Git.Execution;

/// <summary>
/// Identifies one verified remote base64 decoder accepted by the fixed initialization script.
/// </summary>
internal enum SshBase64Decoder
{
    /// <summary>
    /// Uses the GNU-compatible <c>base64 --decode</c> form.
    /// </summary>
    Gnu,

    /// <summary>
    /// Uses the BSD-compatible <c>base64 -D</c> form.
    /// </summary>
    Bsd,

    /// <summary>
    /// Uses the OpenSSL <c>base64 -d -A</c> form.
    /// </summary>
    OpenSsl,
}
