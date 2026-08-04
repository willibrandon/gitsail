using GitSail.Ui;
using System.Text;

namespace GitSail.UnitTests;

/// <summary>
/// Verifies BOM-aware configured file decoding remains terminal safe and byte preserving.
/// </summary>
[TestClass]
public sealed class FileContentPresentationDecoderTests
{
    /// <summary>
    /// Verifies UTF-16 content is split into logical lines without exposing encoding NUL bytes.
    /// </summary>
    [TestMethod]
    public void DecodeLines_WithUtf16Bom_ReturnsLogicalDisplayLines()
    {
        var preamble = Encoding.Unicode.GetPreamble();
        var payload = Encoding.Unicode.GetBytes("first\r\nsecond\n");
        var bytes = preamble.Concat(payload).ToArray();

        var result = FileContentPresentationDecoder.DecodeLines(bytes, "UTF-8");

        Assert.AreEqual("UTF-16 LE", result.EncodingName);
        Assert.IsNull(result.Warning);
        Assert.HasCount(2, result.Lines);
        Assert.AreEqual("first", result.Lines[0]);
        Assert.AreEqual("second", result.Lines[1]);
    }

    /// <summary>
    /// Verifies a UTF-32 BOM is matched before its shared UTF-16 prefix and decoded correctly.
    /// </summary>
    [TestMethod]
    public void DecodeLines_WithUtf32Bom_ReturnsLogicalDisplayLines()
    {
        var preamble = Encoding.UTF32.GetPreamble();
        var payload = Encoding.UTF32.GetBytes("first\nsecond\n");
        var bytes = preamble.Concat(payload).ToArray();

        var result = FileContentPresentationDecoder.DecodeLines(bytes, "UTF-8");

        Assert.AreEqual("UTF-32 LE", result.EncodingName);
        Assert.IsNull(result.Warning);
        Assert.HasCount(2, result.Lines);
        Assert.AreEqual("first", result.Lines[0]);
        Assert.AreEqual("second", result.Lines[1]);
    }

    /// <summary>
    /// Verifies a configured legacy code page is decoded through the static Native AOT-safe provider.
    /// </summary>
    [TestMethod]
    public void DecodeLines_WithConfiguredCodePage_ReturnsDecodedText()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var bytes = Encoding.GetEncoding(1252).GetBytes("café\n");

        var result = FileContentPresentationDecoder.DecodeLines(bytes, "windows-1252");

        Assert.IsNull(result.Warning);
        Assert.HasCount(1, result.Lines);
        Assert.AreEqual("café", result.Lines[0]);
    }

    /// <summary>
    /// Verifies invalid configured bytes remain visible as terminal-safe exact byte tokens.
    /// </summary>
    [TestMethod]
    public void DecodeLines_WithInvalidConfiguredBytes_ReturnsByteTokensAndWarning()
    {
        var result = FileContentPresentationDecoder.DecodeLines([0xff, (byte)'\n'], "UTF-8");

        Assert.IsNotNull(result.Warning);
        Assert.HasCount(1, result.Lines);
        Assert.AreEqual("<0xFF>", result.Lines[0]);
    }
}
