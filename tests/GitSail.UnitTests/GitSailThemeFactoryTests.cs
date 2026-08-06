using GitSail.Domain;
using GitSail.Ui;
using Hex1b.Theming;
using System.Text;

namespace GitSail.UnitTests;

/// <summary>
/// Verifies configured application palettes and terminal color tiers.
/// </summary>
[TestClass]
public sealed class GitSailThemeFactoryTests
{
    /// <summary>
    /// Verifies an explicit light profile produces readable truecolor UI and diff colors.
    /// </summary>
    [TestMethod]
    public void Create_WithLightTruecolorConfiguration_UsesLightPalette()
    {
        var theme = GitSailThemeFactory.Create(
            Configuration(("gitsail.theme", "light"), ("gitsail.colordepth", "truecolor")),
            noColor: null,
            term: "xterm-256color",
            colorTerm: "truecolor",
            windowsTerminalSession: null);

        Assert.AreEqual("GitSail light truecolor", theme.Name);
        Assert.AreEqual(Hex1bColorKind.Rgb, theme.Get(GlobalTheme.BackgroundColor).Kind);
        Assert.AreEqual((byte)255, theme.Get(GlobalTheme.BackgroundColor).R);
        Assert.AreEqual((byte)26, theme.Get(GitSailDiffTheme.AdditionForegroundColor).R);
        Assert.AreEqual((byte)207, theme.Get(GitSailDiffTheme.DeletionForegroundColor).R);
        Assert.IsTrue(theme.IsLocked);
    }

    /// <summary>
    /// Verifies NO_COLOR selects default terminal colors when no explicit depth overrides it.
    /// </summary>
    [TestMethod]
    public void Create_WithNoColorAndAutomaticDepth_UsesMonochromePalette()
    {
        var theme = GitSailThemeFactory.Create(
            new GitConfigurationSnapshot([]),
            noColor: string.Empty,
            term: "xterm-256color",
            colorTerm: "truecolor",
            windowsTerminalSession: null);

        Assert.AreEqual("GitSail monochrome none", theme.Name);
        Assert.IsTrue(theme.Get(GlobalTheme.ForegroundColor).IsDefault);
        Assert.IsTrue(theme.Get(ListTheme.SelectedBackgroundColor).IsDefault);
        Assert.IsTrue(theme.Get(GitSailDiffTheme.AdditionForegroundColor).IsDefault);
        Assert.AreEqual("> ", theme.Get(ListTheme.SelectedIndicator));
    }

    /// <summary>
    /// Verifies an explicit color-depth setting overrides NO_COLOR as requested by the user.
    /// </summary>
    [TestMethod]
    public void Create_WithExplicitSixteenColorDepth_OverridesNoColor()
    {
        var theme = GitSailThemeFactory.Create(
            Configuration(("gitsail.theme", "dark"), ("gitsail.colordepth", "16")),
            noColor: "1",
            term: "dumb",
            colorTerm: null,
            windowsTerminalSession: null);

        Assert.AreEqual("GitSail dark 16", theme.Name);
        Assert.AreEqual(Hex1bColorKind.Standard, theme.Get(GlobalTheme.ForegroundColor).Kind);
        Assert.AreEqual((byte)7, theme.Get(GlobalTheme.ForegroundColor).AnsiIndex);
        Assert.AreEqual(Hex1bColorKind.Standard, theme.Get(GitSailDiffTheme.AdditionForegroundColor).Kind);
    }

    /// <summary>
    /// Verifies the color-blind palette uses distinct indexed blue and orange diff colors.
    /// </summary>
    [TestMethod]
    public void Create_WithColorBlindProfile_UsesBlueOrangeDiffPalette()
    {
        var theme = GitSailThemeFactory.Create(
            Configuration(("gitsail.theme", "color-blind"), ("gitsail.colordepth", "256")),
            noColor: null,
            term: "xterm-256color",
            colorTerm: null,
            windowsTerminalSession: null);

        var addition = theme.Get(GitSailDiffTheme.AdditionForegroundColor);
        var deletion = theme.Get(GitSailDiffTheme.DeletionForegroundColor);
        Assert.AreEqual(Hex1bColorKind.Indexed, addition.Kind);
        Assert.AreEqual((byte)32, addition.AnsiIndex);
        Assert.AreEqual((byte)214, deletion.AnsiIndex);
        Assert.AreNotEqual(addition.AnsiIndex, deletion.AnsiIndex);
    }

    /// <summary>
    /// Verifies registered Git diff foreground and background colors override the selected profile.
    /// </summary>
    [TestMethod]
    public void Create_WithConfiguredDiffColors_AppliesForegroundAndBackground()
    {
        var theme = GitSailThemeFactory.Create(
            Configuration(
                ("gitsail.colordepth", "truecolor"),
                ("color.diff.new", "#123456 #654321 bold"),
                ("color.diff.old", "yellow blue")),
            noColor: null,
            term: "xterm-256color",
            colorTerm: "truecolor",
            windowsTerminalSession: null);

        var additionForeground = theme.Get(GitSailDiffTheme.AdditionForegroundColor);
        var additionBackground = theme.Get(GitSailDiffTheme.AdditionBackgroundColor);
        Assert.AreEqual((byte)0x12, additionForeground.R);
        Assert.AreEqual((byte)0x34, additionForeground.G);
        Assert.AreEqual((byte)0x56, additionForeground.B);
        Assert.AreEqual((byte)0x65, additionBackground.R);
        Assert.AreEqual((byte)0x43, additionBackground.G);
        Assert.AreEqual((byte)0x21, additionBackground.B);
        Assert.AreEqual(Hex1bColorKind.Standard, theme.Get(GitSailDiffTheme.DeletionForegroundColor).Kind);
        Assert.AreEqual((byte)3, theme.Get(GitSailDiffTheme.DeletionForegroundColor).AnsiIndex);
        Assert.AreEqual((byte)4, theme.Get(GitSailDiffTheme.DeletionBackgroundColor).AnsiIndex);
    }

    /// <summary>
    /// Verifies the high-contrast selection pair exceeds the enhanced contrast threshold.
    /// </summary>
    [TestMethod]
    public void Create_WithHighContrastProfile_MeetsSelectionContrastGate()
    {
        var theme = GitSailThemeFactory.Create(
            Configuration(("gitsail.theme", "high-contrast"), ("gitsail.colordepth", "truecolor")),
            noColor: null,
            term: null,
            colorTerm: null,
            windowsTerminalSession: null);

        var foreground = theme.Get(ListTheme.SelectedForegroundColor);
        var background = theme.Get(ListTheme.SelectedBackgroundColor);
        Assert.IsGreaterThanOrEqualTo(7.0, Contrast(foreground, background));
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

    private static double Contrast(Hex1bColor first, Hex1bColor second)
    {
        var firstLuminance = Luminance(first);
        var secondLuminance = Luminance(second);
        return (Math.Max(firstLuminance, secondLuminance) + 0.05) /
            (Math.Min(firstLuminance, secondLuminance) + 0.05);
    }

    private static double Luminance(Hex1bColor color)
        => (0.2126 * Linear(color.R)) + (0.7152 * Linear(color.G)) + (0.0722 * Linear(color.B));

    private static double Linear(byte component)
    {
        var value = component / 255.0;
        return value <= 0.04045
            ? value / 12.92
            : Math.Pow((value + 0.055) / 1.055, 2.4);
    }
}
