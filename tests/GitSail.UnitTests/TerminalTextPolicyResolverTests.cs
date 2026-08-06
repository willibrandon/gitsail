using GitSail.Domain;
using GitSail.Ui;
using System.Text;

namespace GitSail.UnitTests;

/// <summary>
/// Verifies configured terminal Unicode policy and presentation-boundary fallbacks.
/// Covers automatic environment choices, explicit overrides, and ordered UTF-8 blocks.
/// </summary>
[TestClass]
public sealed class TerminalTextPolicyResolverTests
{
    /// <summary>
    /// Verifies an ordinary Unicode terminal uses one-cell ambiguous characters by default.
    /// Leaves terminal output unchanged when no conservative fallback is necessary.
    /// </summary>
    [TestMethod]
    public void Resolve_WithOrdinaryTerminal_UsesUnicodeWidthOne()
    {
        var policy = TerminalTextPolicyResolver.Resolve(
            new GitConfigurationSnapshot([]),
            "xterm-256color",
            "en-US");

        Assert.IsFalse(policy.UseAscii);
        Assert.AreEqual(1, policy.AmbiguousWidth);
        Assert.IsFalse(policy.RequiresTransformation);
    }

    /// <summary>
    /// Verifies automatic policy selects ASCII for a terminal declaring no rich capabilities.
    /// Keeps the resulting conservative transformation explicit and testable.
    /// </summary>
    [TestMethod]
    public void Resolve_WithDumbTerminal_UsesAscii()
    {
        var policy = TerminalTextPolicyResolver.Resolve(
            new GitConfigurationSnapshot([]),
            "dumb",
            "en-US");

        Assert.IsTrue(policy.UseAscii);
        Assert.AreEqual(1, policy.AmbiguousWidth);
    }

    /// <summary>
    /// Verifies an East Asian locale selects width-two ambiguous handling without an override.
    /// Preserves wide CJK text while replacing only ambiguous nonzero-width graphemes.
    /// </summary>
    [TestMethod]
    public void Resolve_WithEastAsianLocale_UsesAmbiguousWidthTwo()
    {
        var policy = TerminalTextPolicyResolver.Resolve(
            new GitConfigurationSnapshot([]),
            "xterm-256color",
            "ja_JP.UTF-8");

        Assert.IsFalse(policy.UseAscii);
        Assert.AreEqual(2, policy.AmbiguousWidth);
    }

    /// <summary>
    /// Verifies explicit Unicode and ambiguous-width settings override automatic environment choices.
    /// Applies repository-scoped typed configuration without changing the retained text.
    /// </summary>
    [TestMethod]
    public void Resolve_WithExplicitOverrides_UsesConfiguredPolicy()
    {
        var policy = TerminalTextPolicyResolver.Resolve(
            Configuration(
                ("gitsail.unicode", "unicode"),
                ("gitsail.ambiguouswidth", "1")),
            "dumb",
            "zh-CN");

        Assert.IsFalse(policy.UseAscii);
        Assert.AreEqual(1, policy.AmbiguousWidth);
    }

    /// <summary>
    /// Verifies ASCII presentation replaces chrome and repository graphemes at equal display width.
    /// Produces only ASCII terminal bytes while leaving the source string untouched.
    /// </summary>
    [TestMethod]
    public void Transform_WithAsciiPolicy_UsesWidthPreservingReplacements()
    {
        const string source = "border │ arrow → ellipsis … snowman ☃ CJK 漢";
        var transformer = new TerminalTextOutputTransformer();

        var transformed = transformer.Transform(
            Encoding.UTF8.GetBytes(source),
            new TerminalTextPolicy(UseAscii: true, AmbiguousWidth: 1));

        Assert.AreEqual(
            "border | arrow > ellipsis . snowman ? CJK ??",
            Encoding.UTF8.GetString(transformed));
    }

    /// <summary>
    /// Verifies width-two mode replaces ambiguous glyphs while retaining ordinary wide text.
    /// Prevents terminal-dependent border and localized-text shifts without guessing cell widths.
    /// </summary>
    [TestMethod]
    public void Transform_WithAmbiguousWidthTwo_ReplacesOnlyAmbiguousVisibleGraphemes()
    {
        var transformer = new TerminalTextOutputTransformer();

        var transformed = transformer.Transform(
            "Latin A Greek Ω border │ CJK 漢"u8,
            new TerminalTextPolicy(UseAscii: false, AmbiguousWidth: 2));

        Assert.AreEqual(
            "Latin A Greek ? border ? CJK 漢",
            Encoding.UTF8.GetString(transformed));
    }

    /// <summary>
    /// Verifies a multibyte output scalar split across ordered writes is reassembled before fallback.
    /// Prevents partial UTF-8 blocks from producing replacement-byte garble in the terminal.
    /// </summary>
    [TestMethod]
    public void Transform_WithSplitUtf8Scalar_ReassemblesBeforeAsciiReplacement()
    {
        var transformer = new TerminalTextOutputTransformer();
        var snowman = "☃"u8.ToArray();

        var first = transformer.Transform(
            snowman.AsSpan(0, 2),
            new TerminalTextPolicy(UseAscii: true, AmbiguousWidth: 1));
        var second = transformer.Transform(
            snowman.AsSpan(2),
            new TerminalTextPolicy(UseAscii: true, AmbiguousWidth: 1));

        Assert.IsEmpty(first);
        Assert.AreEqual("?", Encoding.UTF8.GetString(second));
    }

    /// <summary>
    /// Verifies the versioned East Asian Width table classifies representative scalars exactly.
    /// Covers ambiguous chrome, ordinary ASCII, wide CJK, and a supplementary ambiguous range.
    /// </summary>
    [TestMethod]
    public void IsAmbiguous_WithRepresentativeScalars_ReturnsUnicode17Classification()
    {
        Assert.IsTrue(TerminalEastAsianWidth.IsAmbiguous('Ω'));
        Assert.IsTrue(TerminalEastAsianWidth.IsAmbiguous('│'));
        Assert.IsTrue(TerminalEastAsianWidth.IsAmbiguous(0x1F100));
        Assert.IsFalse(TerminalEastAsianWidth.IsAmbiguous('A'));
        Assert.IsFalse(TerminalEastAsianWidth.IsAmbiguous('漢'));
    }

    private static GitConfigurationSnapshot Configuration(params (string Key, string Value)[] values)
        => new(
        [
            .. values.Select(value => new GitConfigurationEntry(
                GitConfigurationScope.Local,
                GitConfigurationOrigin.FromBytes("file:test"u8),
                GitConfigurationKey.FromBytes(Encoding.UTF8.GetBytes(value.Key)),
                GitConfigurationValue.FromBytes(Encoding.UTF8.GetBytes(value.Value)))),
        ]);
}
