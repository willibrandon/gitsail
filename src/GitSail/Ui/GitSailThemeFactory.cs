using GitSail.Domain;
using Hex1b.Theming;
using System.Globalization;

namespace GitSail.Ui;

/// <summary>
/// Creates complete application and diff palettes from typed Git and terminal settings.
/// </summary>
internal static class GitSailThemeFactory
{
    /// <summary>
    /// Creates the effective light, dark, high-contrast, color-blind, or monochrome palette.
    /// </summary>
    /// <param name="configuration">The effective Git configuration, when one has been loaded.</param>
    /// <param name="noColor">The exact <c>NO_COLOR</c> value, or none when it is absent.</param>
    /// <param name="term">The exact <c>TERM</c> value, or none when it is absent.</param>
    /// <param name="colorTerm">The exact <c>COLORTERM</c> value, or none when it is absent.</param>
    /// <param name="windowsTerminalSession">The exact <c>WT_SESSION</c> value, or none when it is absent.</param>
    /// <returns>A locked theme whose color encoding matches the effective capability tier.</returns>
    internal static Hex1bTheme Create(
        GitConfigurationSnapshot? configuration,
        string? noColor,
        string? term,
        string? colorTerm,
        string? windowsTerminalSession)
    {
        var profile = configuration?.Resolve("gitsail.theme", GitConfigurationScope.Local)
            .EffectiveParsedValue?.Text ?? "auto";
        var depth = ResolveDepth(
            configuration,
            noColor,
            term,
            colorTerm,
            windowsTerminalSession);
        if (depth == "none")
        {
            profile = "monochrome";
        }

        var isLight = profile == "light";
        var isHighContrast = profile == "high-contrast";
        var isColorBlind = profile == "color-blind";
        var foreground = Color(depth, 7, 252,
            isLight ? (20, 24, 28) : (230, 237, 243));
        var background = Color(depth, 0, isLight ? (byte)231 : (byte)233,
            isLight ? (255, 255, 255) : (13, 17, 23));
        var surface = Color(depth, isLight ? (byte)7 : (byte)0, isLight ? (byte)255 : (byte)234,
            isLight ? (246, 248, 250) : (22, 27, 34));
        var surfaceFocused = Color(depth, isLight ? (byte)7 : (byte)0, isLight ? (byte)254 : (byte)236,
            isLight ? (235, 241, 247) : (31, 40, 52));
        var muted = Color(depth, isLight ? (byte)0 : (byte)7, isLight ? (byte)242 : (byte)245,
            isLight ? (87, 96, 106) : (139, 148, 158));
        var accent = Color(depth, isHighContrast ? (byte)3 : (byte)4, isHighContrast ? (byte)226 : (byte)75,
            isHighContrast ? (255, 255, 0) : isLight ? (9, 105, 218) : (88, 166, 255));
        var selectedForeground = isHighContrast
            ? Color(depth, 0, 16, (0, 0, 0))
            : isLight
                ? Color(depth, 7, 231, (255, 255, 255))
                : Color(depth, 0, 16, (0, 0, 0));
        var selectedBackground = accent;
        var hoverBackground = Color(depth, isLight ? (byte)7 : (byte)0, isLight ? (byte)253 : (byte)237,
            isLight ? (225, 232, 240) : (40, 50, 65));
        var additionForeground = Color(depth, isColorBlind ? (byte)4 : (byte)2, isColorBlind ? (byte)32 : (byte)78,
            isColorBlind ? (0, 114, 178) : isLight ? (26, 127, 55) : (86, 211, 100));
        var additionBackground = Color(depth, isColorBlind ? (byte)4 : (byte)2, isColorBlind ? (byte)17 : (byte)22,
            isColorBlind ? (8, 42, 64) : isLight ? (218, 251, 225) : (15, 46, 24));
        var deletionForeground = Color(depth, isColorBlind ? (byte)3 : (byte)1, isColorBlind ? (byte)214 : (byte)203,
            isColorBlind ? (230, 159, 0) : isLight ? (207, 34, 46) : (255, 123, 114));
        var deletionBackground = Color(depth, isColorBlind ? (byte)3 : (byte)1, isColorBlind ? (byte)94 : (byte)52,
            isColorBlind ? (74, 51, 0) : isLight ? (255, 235, 233) : (59, 15, 20));
        var theme = new Hex1bTheme($"GitSail {profile} {depth}")
            .Set(GlobalTheme.ForegroundColor, foreground)
            .Set(GlobalTheme.BackgroundColor, background)
            .Set(EditorTheme.ForegroundColor, foreground)
            .Set(EditorTheme.BackgroundColor, background)
            .Set(EditorTheme.CursorForegroundColor, selectedForeground)
            .Set(EditorTheme.CursorBackgroundColor, selectedBackground)
            .Set(EditorTheme.SelectionForegroundColor, selectedForeground)
            .Set(EditorTheme.SelectionBackgroundColor, selectedBackground)
            .Set(EditorTheme.LineNumberForegroundColor, muted)
            .Set(GutterTheme.LineNumberForegroundColor, muted)
            .Set(GutterTheme.BackgroundColor, background)
            .Set(TextBoxTheme.ForegroundColor, foreground)
            .Set(TextBoxTheme.BackgroundColor, background)
            .Set(TextBoxTheme.FocusedForegroundColor, foreground)
            .Set(TextBoxTheme.FillBackgroundColor, surface)
            .Set(TextBoxTheme.FocusedFillBackgroundColor, surfaceFocused)
            .Set(TextBoxTheme.CursorForegroundColor, selectedForeground)
            .Set(TextBoxTheme.CursorBackgroundColor, selectedBackground)
            .Set(TextBoxTheme.SelectionForegroundColor, selectedForeground)
            .Set(TextBoxTheme.SelectionBackgroundColor, selectedBackground)
            .Set(ListTheme.ForegroundColor, foreground)
            .Set(ListTheme.BackgroundColor, background)
            .Set(ListTheme.SelectedForegroundColor, selectedForeground)
            .Set(ListTheme.SelectedBackgroundColor, selectedBackground)
            .Set(ListTheme.HoveredForegroundColor, foreground)
            .Set(ListTheme.HoveredBackgroundColor, hoverBackground)
            .Set(ListTheme.SelectedIndicator, "> ")
            .Set(ButtonTheme.ForegroundColor, foreground)
            .Set(ButtonTheme.BackgroundColor, surface)
            .Set(ButtonTheme.FocusedForegroundColor, selectedForeground)
            .Set(ButtonTheme.FocusedBackgroundColor, selectedBackground)
            .Set(ButtonTheme.HoveredForegroundColor, foreground)
            .Set(ButtonTheme.HoveredBackgroundColor, hoverBackground)
            .Set(BorderTheme.BorderColor, muted)
            .Set(BorderTheme.TitleColor, foreground)
            .Set(InfoBarTheme.ForegroundColor, foreground)
            .Set(InfoBarTheme.BackgroundColor, surface)
            .Set(MenuTheme.ForegroundColor, foreground)
            .Set(MenuTheme.BackgroundColor, surface)
            .Set(MenuTheme.BorderColor, muted)
            .Set(MenuBarTheme.ForegroundColor, foreground)
            .Set(MenuBarTheme.BackgroundColor, surface)
            .Set(MenuBarTheme.FocusedForegroundColor, selectedForeground)
            .Set(MenuBarTheme.FocusedBackgroundColor, selectedBackground)
            .Set(MenuBarTheme.HoveredForegroundColor, foreground)
            .Set(MenuBarTheme.HoveredBackgroundColor, hoverBackground)
            .Set(MenuBarTheme.AcceleratorForegroundColor, accent)
            .Set(MenuBarTheme.AcceleratorBackgroundColor, surface)
            .Set(WindowTheme.TitleBarForeground, foreground)
            .Set(WindowTheme.TitleBarBackground, surface)
            .Set(WindowTheme.TitleBarActiveForeground, selectedForeground)
            .Set(WindowTheme.TitleBarActiveBackground, selectedBackground)
            .Set(WindowTheme.BorderColor, muted)
            .Set(WindowTheme.BorderActiveColor, accent)
            .Set(WindowTheme.ContentBackground, background)
            .Set(WindowTheme.CloseButtonForeground, foreground)
            .Set(WindowTheme.CloseButtonHoverBackground, hoverBackground)
            .Set(WindowTheme.ResizeThumbColor, accent)
            .Set(ScrollTheme.TrackColor, muted)
            .Set(ScrollTheme.ThumbColor, accent)
            .Set(ScrollTheme.FocusedThumbColor, selectedBackground)
            .Set(SplitterTheme.DividerColor, muted)
            .Set(SplitterTheme.FocusedDividerColor, accent)
            .Set(SplitterTheme.ThumbColor, accent)
            .Set(TabPanelTheme.ContentForegroundColor, foreground)
            .Set(TabPanelTheme.ContentBackgroundColor, background)
            .Set(OverlayTheme.ForegroundColor, foreground)
            .Set(OverlayTheme.BackgroundColor, surface)
            .Set(OverlayTheme.BorderColor, muted)
            .Set(OverlayTheme.TitleForegroundColor, foreground)
            .Set(CheckboxTheme.ForegroundColor, foreground)
            .Set(CheckboxTheme.BackgroundColor, background)
            .Set(CheckboxTheme.BoxBackgroundColor, surface)
            .Set(CheckboxTheme.FocusedForegroundColor, selectedForeground)
            .Set(CheckboxTheme.FocusedBackgroundColor, selectedBackground)
            .Set(CheckboxTheme.HoveredForegroundColor, foreground)
            .Set(CheckboxTheme.HoveredBackgroundColor, hoverBackground)
            .Set(CheckboxTheme.CheckMarkColor, accent)
            .Set(CheckboxTheme.IndeterminateColor, accent)
            .Set(ToggleSwitchTheme.FocusedSelectedForegroundColor, selectedForeground)
            .Set(ToggleSwitchTheme.FocusedSelectedBackgroundColor, selectedBackground)
            .Set(ToggleSwitchTheme.UnfocusedSelectedForegroundColor, foreground)
            .Set(ToggleSwitchTheme.UnfocusedSelectedBackgroundColor, accent)
            .Set(ToggleSwitchTheme.UnselectedForegroundColor, foreground)
            .Set(ToggleSwitchTheme.UnselectedBackgroundColor, surface)
            .Set(ProgressTheme.FilledForegroundColor, accent)
            .Set(ProgressTheme.FilledBackgroundColor, background)
            .Set(ProgressTheme.EmptyForegroundColor, muted)
            .Set(ProgressTheme.EmptyBackgroundColor, background)
            .Set(ProgressTheme.IndeterminateForegroundColor, accent)
            .Set(ProgressTheme.IndeterminateBackgroundColor, background)
            .Set(RescueTheme.ForegroundColor, foreground)
            .Set(RescueTheme.BackgroundColor, background)
            .Set(RescueTheme.BorderColor, deletionForeground)
            .Set(RescueTheme.TitleColor, deletionForeground)
            .Set(RescueTheme.SeparatorColor, muted)
            .Set(RescueTheme.ButtonForegroundColor, foreground)
            .Set(RescueTheme.ButtonBackgroundColor, surface)
            .Set(RescueTheme.ButtonFocusedForegroundColor, selectedForeground)
            .Set(RescueTheme.ButtonFocusedBackgroundColor, selectedBackground)
            .Set(RescueTheme.ErrorTypeColor, deletionForeground)
            .Set(RescueTheme.StackTraceColor, muted)
            .Set(RescueTheme.PhaseColor, accent)
            .Set(GitSailDiffTheme.ContextForegroundColor, foreground)
            .Set(GitSailDiffTheme.ContextBackgroundColor, background)
            .Set(GitSailDiffTheme.HeaderForegroundColor, accent)
            .Set(GitSailDiffTheme.HeaderBackgroundColor, background)
            .Set(GitSailDiffTheme.MetadataForegroundColor, muted)
            .Set(GitSailDiffTheme.MetadataBackgroundColor, background)
            .Set(GitSailDiffTheme.OldFileForegroundColor, deletionForeground)
            .Set(GitSailDiffTheme.OldFileBackgroundColor, background)
            .Set(GitSailDiffTheme.NewFileForegroundColor, additionForeground)
            .Set(GitSailDiffTheme.NewFileBackgroundColor, background)
            .Set(GitSailDiffTheme.HunkForegroundColor, accent)
            .Set(GitSailDiffTheme.HunkBackgroundColor, background)
            .Set(GitSailDiffTheme.FunctionForegroundColor, foreground)
            .Set(GitSailDiffTheme.FunctionBackgroundColor, background)
            .Set(GitSailDiffTheme.AdditionForegroundColor, additionForeground)
            .Set(GitSailDiffTheme.AdditionBackgroundColor, additionBackground)
            .Set(GitSailDiffTheme.DeletionForegroundColor, deletionForeground)
            .Set(GitSailDiffTheme.DeletionBackgroundColor, deletionBackground)
            .Set(GitSailDiffTheme.AddedRangeBackgroundColor, additionBackground)
            .Set(GitSailDiffTheme.RemovedRangeBackgroundColor, deletionBackground)
            .Set(GitSailDiffTheme.WhitespaceForegroundColor, selectedForeground)
            .Set(GitSailDiffTheme.WhitespaceBackgroundColor, deletionForeground);
        ApplyConfiguredDiffColor(
            theme,
            configuration,
            depth,
            "color.diff.plain",
            GitSailDiffTheme.ContextForegroundColor,
            GitSailDiffTheme.ContextBackgroundColor);
        ApplyConfiguredDiffColor(
            theme,
            configuration,
            depth,
            "color.diff.context",
            GitSailDiffTheme.ContextForegroundColor,
            GitSailDiffTheme.ContextBackgroundColor);
        ApplyConfiguredDiffColor(
            theme,
            configuration,
            depth,
            "color.diff.meta",
            GitSailDiffTheme.HeaderForegroundColor,
            GitSailDiffTheme.HeaderBackgroundColor);
        ApplyConfiguredDiffColor(
            theme,
            configuration,
            depth,
            "color.diff.commit",
            GitSailDiffTheme.HeaderForegroundColor,
            GitSailDiffTheme.HeaderBackgroundColor);
        ApplyConfiguredDiffColor(
            theme,
            configuration,
            depth,
            "color.diff.frag",
            GitSailDiffTheme.HunkForegroundColor,
            GitSailDiffTheme.HunkBackgroundColor);
        ApplyConfiguredDiffColor(
            theme,
            configuration,
            depth,
            "color.diff.func",
            GitSailDiffTheme.FunctionForegroundColor,
            GitSailDiffTheme.FunctionBackgroundColor);
        ApplyConfiguredDiffColor(
            theme,
            configuration,
            depth,
            "color.diff.old",
            GitSailDiffTheme.DeletionForegroundColor,
            GitSailDiffTheme.DeletionBackgroundColor);
        ApplyConfiguredDiffColor(
            theme,
            configuration,
            depth,
            "color.diff.old",
            GitSailDiffTheme.OldFileForegroundColor,
            GitSailDiffTheme.OldFileBackgroundColor);
        ApplyConfiguredDiffColor(
            theme,
            configuration,
            depth,
            "color.diff.new",
            GitSailDiffTheme.AdditionForegroundColor,
            GitSailDiffTheme.AdditionBackgroundColor);
        ApplyConfiguredDiffColor(
            theme,
            configuration,
            depth,
            "color.diff.new",
            GitSailDiffTheme.NewFileForegroundColor,
            GitSailDiffTheme.NewFileBackgroundColor);
        ApplyConfiguredDiffColor(
            theme,
            configuration,
            depth,
            "color.diff.whitespace",
            GitSailDiffTheme.WhitespaceForegroundColor,
            GitSailDiffTheme.WhitespaceBackgroundColor);
        return theme.Lock();
    }

    private static void ApplyConfiguredDiffColor(
        Hex1bTheme theme,
        GitConfigurationSnapshot? configuration,
        string depth,
        string key,
        Hex1bThemeElement<Hex1bColor> foregroundElement,
        Hex1bThemeElement<Hex1bColor> backgroundElement)
    {
        if (configuration?.Resolve(key, GitConfigurationScope.Local)
                .EffectiveParsedValue?.Items is not { IsDefaultOrEmpty: false } items)
        {
            return;
        }

        var colors = items
            .Where(static token => !IsColorAttribute(token))
            .Take(2)
            .ToArray();
        if (colors.Length >= 1 && TryParseConfiguredColor(colors[0], depth, out var foreground))
        {
            theme.Set(foregroundElement, foreground);
        }

        if (colors.Length == 2 && TryParseConfiguredColor(colors[1], depth, out var background))
        {
            theme.Set(backgroundElement, background);
        }
    }

    private static bool TryParseConfiguredColor(
        string token,
        string depth,
        out Hex1bColor color)
    {
        var canonical = token.ToLowerInvariant();
        if (depth == "none" || canonical is "normal" or "default")
        {
            color = Hex1bColor.Default;
            return true;
        }

        var standardIndex = canonical switch
        {
            "black" => 0,
            "red" => 1,
            "green" => 2,
            "yellow" => 3,
            "blue" => 4,
            "magenta" => 5,
            "cyan" => 6,
            "white" => 7,
            _ => -1,
        };
        if (standardIndex >= 0)
        {
            var rgb = StandardRgb(standardIndex);
            color = Hex1bColor.FromStandard(
                checked((byte)standardIndex),
                rgb.Red,
                rgb.Green,
                rgb.Blue);
            return true;
        }

        if (byte.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
        {
            var rgb = IndexedRgb(index);
            color = depth == "16"
                ? ToStandard(rgb.Red, rgb.Green, rgb.Blue)
                : Hex1bColor.FromIndexed(index, rgb.Red, rgb.Green, rgb.Blue);
            return true;
        }

        if (token.Length == 7 && token[0] == '#' &&
            byte.TryParse(token.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var red) &&
            byte.TryParse(token.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var green) &&
            byte.TryParse(token.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var blue))
        {
            color = depth switch
            {
                "16" => ToStandard(red, green, blue),
                "256" => ToIndexed(red, green, blue),
                _ => Hex1bColor.FromRgb(red, green, blue),
            };
            return true;
        }

        color = default;
        return false;
    }

    private static bool IsColorAttribute(string token)
        => token.ToLowerInvariant() is
            "bold" or "dim" or "ul" or "blink" or "reverse" or "italic" or "strike" or
            "nobold" or "nodim" or "noul" or "noblink" or "noreverse" or "noitalic" or "nostrike";

    private static Hex1bColor ToStandard(byte red, byte green, byte blue)
    {
        var bestIndex = 0;
        var bestDistance = int.MaxValue;
        for (var index = 0; index < 8; index++)
        {
            var candidate = StandardRgb(index);
            var distance = Distance(red, green, blue, candidate.Red, candidate.Green, candidate.Blue);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestIndex = index;
            }
        }

        var rgb = StandardRgb(bestIndex);
        return Hex1bColor.FromStandard(checked((byte)bestIndex), rgb.Red, rgb.Green, rgb.Blue);
    }

    private static Hex1bColor ToIndexed(byte red, byte green, byte blue)
    {
        var redIndex = checked((byte)Math.Round(red / 255.0 * 5.0));
        var greenIndex = checked((byte)Math.Round(green / 255.0 * 5.0));
        var blueIndex = checked((byte)Math.Round(blue / 255.0 * 5.0));
        var index = checked((byte)(16 + (36 * redIndex) + (6 * greenIndex) + blueIndex));
        var rgb = IndexedRgb(index);
        return Hex1bColor.FromIndexed(index, rgb.Red, rgb.Green, rgb.Blue);
    }

    private static (byte Red, byte Green, byte Blue) StandardRgb(int index)
        => index switch
        {
            0 => (0, 0, 0),
            1 => (205, 49, 49),
            2 => (13, 188, 121),
            3 => (229, 229, 16),
            4 => (36, 114, 200),
            5 => (188, 63, 188),
            6 => (17, 168, 205),
            _ => (229, 229, 229),
        };

    private static (byte Red, byte Green, byte Blue) IndexedRgb(byte index)
    {
        if (index < 8)
        {
            return StandardRgb(index);
        }

        if (index < 16)
        {
            var standard = StandardRgb(index - 8);
            return (
                checked((byte)Math.Min(255, standard.Red + 50)),
                checked((byte)Math.Min(255, standard.Green + 50)),
                checked((byte)Math.Min(255, standard.Blue + 50)));
        }

        if (index < 232)
        {
            var cube = index - 16;
            return (
                CubeComponent(cube / 36),
                CubeComponent((cube / 6) % 6),
                CubeComponent(cube % 6));
        }

        var gray = checked((byte)(8 + ((index - 232) * 10)));
        return (gray, gray, gray);
    }

    private static byte CubeComponent(int component)
        => checked((byte)(component == 0 ? 0 : 55 + (component * 40)));

    private static int Distance(
        byte firstRed,
        byte firstGreen,
        byte firstBlue,
        byte secondRed,
        byte secondGreen,
        byte secondBlue)
        => ((firstRed - secondRed) * (firstRed - secondRed)) +
            ((firstGreen - secondGreen) * (firstGreen - secondGreen)) +
            ((firstBlue - secondBlue) * (firstBlue - secondBlue));

    private static string ResolveDepth(
        GitConfigurationSnapshot? configuration,
        string? noColor,
        string? term,
        string? colorTerm,
        string? windowsTerminalSession)
    {
        var configured = configuration?.Resolve("gitsail.colordepth", GitConfigurationScope.Local)
            .EffectiveParsedValue?.Text ?? "auto";
        if (configured != "auto")
        {
            return configured;
        }

        if (noColor is not null || string.Equals(term, "dumb", StringComparison.OrdinalIgnoreCase))
        {
            return "none";
        }

        if (colorTerm?.Contains("truecolor", StringComparison.OrdinalIgnoreCase) == true ||
            colorTerm?.Contains("24bit", StringComparison.OrdinalIgnoreCase) == true ||
            !string.IsNullOrEmpty(windowsTerminalSession))
        {
            return "truecolor";
        }

        return term?.Contains("256color", StringComparison.OrdinalIgnoreCase) == true
            ? "256"
            : "16";
    }

    private static Hex1bColor Color(
        string depth,
        byte standardIndex,
        byte indexed,
        (int Red, int Green, int Blue) rgb)
    {
        var red = checked((byte)rgb.Red);
        var green = checked((byte)rgb.Green);
        var blue = checked((byte)rgb.Blue);
        return depth switch
        {
            "none" => Hex1bColor.Default,
            "16" => Hex1bColor.FromStandard(standardIndex, red, green, blue),
            "256" => Hex1bColor.FromIndexed(indexed, red, green, blue),
            _ => Hex1bColor.FromRgb(red, green, blue),
        };
    }
}
