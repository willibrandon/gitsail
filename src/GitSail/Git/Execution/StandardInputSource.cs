namespace GitSail.Git.Execution;

/// <summary>
/// Owns the exact bytes supplied to a child process through standard input.
/// </summary>
internal sealed class StandardInputSource
{
    private readonly byte[] _bytes;

    private StandardInputSource(byte[] bytes)
    {
        _bytes = bytes;
    }

    /// <summary>
    /// Gets whether the child receives any standard-input bytes.
    /// </summary>
    internal bool HasBytes => _bytes.Length != 0;

    /// <summary>
    /// Creates an empty standard-input source.
    /// </summary>
    /// <returns>An empty source whose stream is closed immediately after launch.</returns>
    internal static StandardInputSource Empty()
        => new([]);

    /// <summary>
    /// Creates a standard-input source that owns a copy of exact bytes.
    /// </summary>
    /// <param name="bytes">The bytes to write before closing standard input.</param>
    /// <returns>The owned standard-input source.</returns>
    internal static StandardInputSource FromBytes(ReadOnlySpan<byte> bytes)
        => new(bytes.ToArray());

    /// <summary>
    /// Gets the exact bytes owned by this source.
    /// </summary>
    /// <returns>A read-only memory view over the source-owned bytes.</returns>
    internal ReadOnlyMemory<byte> GetBytes()
        => _bytes;
}
