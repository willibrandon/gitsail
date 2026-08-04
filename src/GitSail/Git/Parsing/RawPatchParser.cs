using GitSail.Domain;

namespace GitSail.Git.Parsing;

/// <summary>
/// Builds a strict byte-offset index for complete unified hunks in one raw file patch.
/// </summary>
internal static class RawPatchParser
{
    /// <summary>
    /// Parses one complete non-binary file patch without decoding its content bytes.
    /// </summary>
    /// <param name="patch">The exact file-patch bytes beginning with its diff header.</param>
    /// <returns>The validated header, hunk, and content-line index.</returns>
    internal static RawPatchIndex Parse(ReadOnlySpan<byte> patch)
    {
        var builder = new RawPatchIndexBuilder(0);
        var offset = 0;
        while (offset < patch.Length)
        {
            var remaining = patch[offset..];
            var newline = remaining.IndexOf((byte)'\n');
            var contentLength = newline < 0 ? remaining.Length : newline;
            var totalLength = newline < 0 ? contentLength : contentLength + 1;
            var content = remaining[..contentLength];
            if (!content.IsEmpty && content[^1] == (byte)'\r')
            {
                content = content[..^1];
            }

            builder.ProcessLine(content, offset, totalLength);
            offset += totalLength;
        }

        return builder.Complete(patch.Length);
    }
}
