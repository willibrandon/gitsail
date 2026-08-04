using GitSail.Ui;
using System.Text;

namespace GitSail.UnitTests;

/// <summary>
/// Verifies terminal-safe patch presentation without altering the retained raw-byte source.
/// </summary>
[TestClass]
public sealed class RawPatchPresentationDecoderTests
{
    /// <summary>
    /// Verifies line preservation and visible tokens for controls, invalid bytes, and bidi formatting.
    /// </summary>
    [TestMethod]
    public void Decode_WithUnsafeAndInvalidBytes_ProducesVisibleSafeText()
    {
        byte[] bytes =
        [
            .. "line\r\n"u8,
            0x1b,
            0xff,
            .. Encoding.UTF8.GetBytes("\u202e"),
            (byte)'\n',
        ];

        var text = RawPatchPresentationDecoder.Decode(bytes, isTruncated: false);

        Assert.AreEqual("line\n<U+001B><0xFF><U+202E>\n", text);
    }

    /// <summary>
    /// Verifies a bounded prefix receives an honest truncation marker on its own display line.
    /// </summary>
    [TestMethod]
    public void Decode_WithTruncatedPrefix_AppendsTruncationMarker()
    {
        var text = RawPatchPresentationDecoder.Decode("partial"u8, isTruncated: true);

        Assert.AreEqual(
            "partial\n<patch presentation truncated; exact bytes remain available>",
            text);
    }
}
