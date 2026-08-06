using GitSail.Domain;

namespace GitSail.UnitTests;

/// <summary>
/// Verifies typed configuration validation and executable diff-option rejection.
/// </summary>
[TestClass]
public sealed class GitConfigurationValueValidatorTests
{
    /// <summary>
    /// Verifies Git boolean spellings are canonicalized and invalid text remains invalid.
    /// </summary>
    /// <param name="text">The configured boolean spelling.</param>
    /// <param name="expected">The expected canonical value, or none for invalid input.</param>
    [TestMethod]
    [DataRow("yes", true)]
    [DataRow("OFF", false)]
    [DataRow("1", true)]
    [DataRow("", false)]
    [DataRow("sometimes", null)]
    public void TryParseText_WithBoolean_ReturnsCanonicalValue(string text, bool? expected)
    {
        var definition = GitConfigurationRegistry.Find("gui.trustmtime")!;

        var valid = GitConfigurationValueValidator.TryParseText(
            definition,
            text,
            out var parsed,
            out var error);

        Assert.AreEqual(expected is not null, valid, error);
        Assert.AreEqual(expected, parsed?.BooleanValue);
    }

    /// <summary>
    /// Verifies bounded Git integers accept suffixes while retaining the declared range.
    /// </summary>
    [TestMethod]
    public void TryParseText_WithIntegerSuffixAndBounds_EnforcesDefinition()
    {
        var unbounded = GitConfigurationRegistry.Find("gui.maxfilesdisplayed")!;
        var bounded = GitConfigurationRegistry.Find("gitsail.renamethreshold")!;

        Assert.IsTrue(GitConfigurationValueValidator.TryParseText(
            unbounded,
            "2k",
            out var parsed,
            out var suffixError),
            suffixError);
        Assert.AreEqual(2048L, parsed!.IntegerValue);
        Assert.IsFalse(GitConfigurationValueValidator.TryParseText(
            bounded,
            "101",
            out _,
            out var boundsError));
        StringAssert.Contains(boundsError, "0 through 100");
    }

    /// <summary>
    /// Verifies the diff-option parser accepts compatible presentation options and rejects execution or output controls.
    /// </summary>
    /// <param name="text">The configured option sequence.</param>
    /// <param name="expected">Whether the complete sequence is allowed.</param>
    [TestMethod]
    [DataRow("-U8 --ignore-space-at-eol --histogram --stat", true)]
    [DataRow("{--anchored=public method} --unified=12", true)]
    [DataRow("--ext-diff", false)]
    [DataRow("--textconv", false)]
    [DataRow("--color=always", false)]
    [DataRow("--output=/tmp/captured", false)]
    [DataRow("--relative=outside", false)]
    public void TryParseText_WithDiffOptions_UsesStrictAllowlist(string text, bool expected)
    {
        var definition = GitConfigurationRegistry.Find("gui.diffopts")!;

        var actual = GitConfigurationValueValidator.TryParseText(
            definition,
            text,
            out _,
            out _);

        Assert.AreEqual(expected, actual);
    }

    /// <summary>
    /// Verifies a caller-supplied oversized token fails validation without integer overflow.
    /// </summary>
    [TestMethod]
    public void TryValidateItems_WithOversizedToken_ReturnsFalse()
    {
        var actual = GitDiffOptions.TryValidateItems(
            [new string('x', 4097)],
            out var error);

        Assert.IsFalse(actual);
        StringAssert.Contains(error, "4096-character limit");
    }

    /// <summary>
    /// Verifies key chords and versioned JSON records reject malformed values.
    /// </summary>
    [TestMethod]
    public void TryParseText_WithStructuredValues_RequiresKnownShape()
    {
        var keymap = GitConfigurationRegistry.Find("gitsail.keymap.repository.refresh")!;
        var layout = GitConfigurationRegistry.Find("gitsail.layout")!;
        var capability = GitConfigurationRegistry.Find(
            "gitsail.trustedrepository.0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")!;

        Assert.IsTrue(GitConfigurationValueValidator.TryParseText(
            keymap,
            "Ctrl+R, F5",
            out _,
            out var chordError),
            chordError);
        Assert.IsFalse(GitConfigurationValueValidator.TryParseText(
            keymap,
            "Command+R",
            out _,
            out _));
        Assert.IsTrue(GitConfigurationValueValidator.TryParseText(
            layout,
            "{\"version\":1,\"split\":44}",
            out _,
            out var layoutError),
            layoutError);
        Assert.IsFalse(GitConfigurationValueValidator.TryParseText(
            layout,
            "{\"version\":2}",
            out _,
            out _));
        var commandHash = new string('a', 64);
        Assert.IsTrue(GitConfigurationValueValidator.TryParseText(
            capability,
            $"{{\"version\":1,\"commands\":[\"{commandHash}\"]}}",
            out _,
            out var capabilityError),
            capabilityError);
        Assert.IsFalse(GitConfigurationValueValidator.TryParseText(
            capability,
            "{\"version\":1}",
            out _,
            out _));
        Assert.IsFalse(GitConfigurationValueValidator.TryParseText(
            capability,
            $"{{\"version\":1,\"version\":1,\"commands\":[\"{commandHash}\"]}}",
            out _,
            out _));
    }

    /// <summary>
    /// Verifies native-path configuration retains non-UTF-8 Unix bytes without decoding them as text.
    /// </summary>
    [TestMethod]
    public void TryParse_WithNativePath_RetainsPlatformRepresentation()
    {
        var definition = GitConfigurationRegistry.Find("gui.recentrepo")!;
        var value = GitConfigurationValue.FromBytes([0x66, 0x6f, 0x80]);

        var valid = GitConfigurationValueValidator.TryParse(
            definition,
            value,
            out var parsed,
            out var error);

        Assert.AreEqual(!OperatingSystem.IsWindows(), valid, error);
        if (!OperatingSystem.IsWindows())
        {
            CollectionAssert.AreEqual(value.GetBytes().ToArray(), parsed!.NativePath!.GetUnixBytes().ToArray());
        }
    }
}
