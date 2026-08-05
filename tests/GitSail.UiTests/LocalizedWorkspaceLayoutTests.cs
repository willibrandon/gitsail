using GitSail.CommandLine;
using GitSail.Localization.Generated;
using GitSail.Ui;
using Hex1b;
using Hex1b.Automation;
using Hex1b.Input;
using System.Globalization;

namespace GitSail.UiTests;

/// <summary>
/// Verifies localized workspace text remains readable at every supported responsive breakpoint.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class LocalizedWorkspaceLayoutTests
{
    /// <summary>
    /// Verifies the minimum-size fallback explains the required dimensions in the active locale.
    /// </summary>
    [TestMethod]
    public async Task Workspace_BelowMinimumSize_RendersLocalizedResizeGuidance()
    {
        const string locale = "ja-JP";
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        var culture = CultureInfo.GetCultureInfo(locale);
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        var session = new FakeRepositoryWorkspaceSession(
            FakeRepositoryWorkspaceSession.CreateUnstagedEntry("localized.txt"));
        var view = new RepositoryWorkspaceView(
            new GitSailShellOptions(ApplicationMode.Gui, WorkingDirectory: null),
            session,
            CancellationToken.None);
        Hex1bApp? application = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        try
        {
            await using var terminal = Hex1bTerminal.CreateBuilder()
                .WithHeadless()
                .WithDimensions(59, 17)
                .WithHex1bApp(
                    terminalOptions => terminalOptions.EnableMouse = true,
                    createdApplication =>
                    {
                        application = createdApplication;
                        view.Attach(createdApplication);
                        return view.Build;
                    })
                .Build();
            var runTask = terminal.RunAsync(timeout.Token);
            var automator = new Hex1bTerminalAutomator(terminal, TimeSpan.FromSeconds(3));

            try
            {
                await automator.WaitUntilTextAsync(
                    AppMessages.WorkspaceResizeTitleForLocale(locale),
                    TimeSpan.FromSeconds(3));
                using var snapshot = automator.CreateSnapshot();
                Assert.IsTrue(snapshot.ContainsText("GitSail には幅 60 列、高さ 18"));
                Assert.IsTrue(snapshot.ContainsText("行以上のターミナルが必要です。"));
                Assert.IsTrue(snapshot.ContainsText("ターミナルのサイズを変更してリポジトリ画面に戻ってくださ"));
                Assert.IsTrue(snapshot.ContainsText("い。"));
                Assert.IsTrue(snapshot.ContainsText("F1 ヘルプ、F2 コマンド、F10 メニュー、Ctrl+Q"));
                Assert.IsTrue(snapshot.ContainsText("終了は引き続き使用できます。"));
                Assert.IsFalse(snapshot.ContainsText(AppMessages.WorkspaceActionCommitForLocale(locale)));

                for (var row = 0; row < snapshot.Height; row++)
                {
                    Assert.IsLessThanOrEqualTo(
                        snapshot.Width,
                        DisplayWidth.GetStringWidth(snapshot.GetLine(row).TrimEnd()),
                        $"Locale '{locale}' overflowed terminal row {row} at 59x17.");
                }
            }
            finally
            {
                application?.RequestStop();
                await runTask;
                view.Detach();
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    /// <summary>
    /// Verifies translated and expansion-pseudo text remains complete within supported terminal bounds.
    /// </summary>
    /// <param name="width">The terminal width under test.</param>
    /// <param name="height">The terminal height under test.</param>
    /// <param name="locale">The UI culture used to build the workspace.</param>
    [TestMethod]
    [DataRow(60, 18, "ja-JP")]
    [DataRow(80, 24, "de-DE")]
    [DataRow(120, 30, "en-XA")]
    public async Task Workspace_WithLocalizedText_FitsResponsiveBreakpoint(
        int width,
        int height,
        string locale)
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        var culture = CultureInfo.GetCultureInfo(locale);
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        var session = new FakeRepositoryWorkspaceSession(
            FakeRepositoryWorkspaceSession.CreateUnstagedEntry("localized.txt"));
        var view = new RepositoryWorkspaceView(
            new GitSailShellOptions(ApplicationMode.Gui, WorkingDirectory: null),
            session,
            CancellationToken.None);
        Hex1bApp? application = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        try
        {
            await using var terminal = Hex1bTerminal.CreateBuilder()
                .WithHeadless()
                .WithDimensions(width, height)
                .WithHex1bApp(
                    terminalOptions => terminalOptions.EnableMouse = true,
                    createdApplication =>
                    {
                        application = createdApplication;
                        view.Attach(createdApplication);
                        return view.Build;
                    })
                .Build();
            var runTask = terminal.RunAsync(timeout.Token);
            var automator = new Hex1bTerminalAutomator(terminal, TimeSpan.FromSeconds(3));
            var unstagedTitle = $"{AppMessages.WorkspaceSectionUnstagedForLocale(locale)} (1)";

            try
            {
                await automator.WaitUntilTextAsync(unstagedTitle, TimeSpan.FromSeconds(3));
                using var snapshot = automator.CreateSnapshot();
                Assert.IsFalse(snapshot.ContainsText("Terminal too small"));
                Assert.IsTrue(snapshot.ContainsText(AppMessages.WorkspaceLabelFindForLocale(locale)));
                Assert.IsTrue(snapshot.ContainsText(AppMessages.WorkspaceActionMenuForLocale(locale)));
                Assert.IsTrue(snapshot.ContainsText(AppMessages.WorkspaceActionRefreshForLocale(locale)));
                Assert.IsTrue(snapshot.ContainsText(AppMessages.WorkspaceActionQuitForLocale(locale)));

                if (width == 60)
                {
                    Assert.IsTrue(snapshot.ContainsText(AppMessages.WorkspaceSectionChangesForLocale(locale)));
                    Assert.IsTrue(snapshot.ContainsText(AppMessages.WorkspaceSectionDiffForLocale(locale)));
                    Assert.IsTrue(snapshot.ContainsText(AppMessages.WorkspaceSectionCommitForLocale(locale)));
                }
                else
                {
                    Assert.IsTrue(snapshot.ContainsText(
                        AppMessages.WorkspaceSectionCommitMessageForLocale(locale)));
                    Assert.IsTrue(snapshot.ContainsText(
                        $"{AppMessages.WorkspaceSectionUnstagedForLocale(locale)}: localized.txt"));
                }

                for (var row = 0; row < snapshot.Height; row++)
                {
                    Assert.IsLessThanOrEqualTo(
                        snapshot.Width,
                        DisplayWidth.GetStringWidth(snapshot.GetLine(row).TrimEnd()),
                        $"Locale '{locale}' overflowed terminal row {row} at {width}x{height}.");
                }
            }
            finally
            {
                application?.RequestStop();
                await runTask;
                view.Detach();
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    /// <summary>
    /// Verifies the primary workspace diff-search row uses the active locale.
    /// </summary>
    [TestMethod]
    public async Task WorkspaceDiffSearch_WithJapaneseLocale_RendersLocalizedControls()
    {
        const string locale = "ja-JP";
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        var culture = CultureInfo.GetCultureInfo(locale);
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        var session = new FakeRepositoryWorkspaceSession(
            FakeRepositoryWorkspaceSession.CreateUnstagedEntry("localized.txt"));
        var view = new RepositoryWorkspaceView(
            new GitSailShellOptions(ApplicationMode.Gui, WorkingDirectory: null),
            session,
            CancellationToken.None);
        Hex1bApp? application = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        try
        {
            await using var terminal = Hex1bTerminal.CreateBuilder()
                .WithHeadless()
                .WithDimensions(80, 24)
                .WithHex1bApp(
                    terminalOptions => terminalOptions.EnableMouse = true,
                    createdApplication =>
                    {
                        application = createdApplication;
                        view.Attach(createdApplication);
                        return view.Build;
                    })
                .Build();
            var runTask = terminal.RunAsync(timeout.Token);
            var automator = new Hex1bTerminalAutomator(terminal, TimeSpan.FromSeconds(3));

            try
            {
                await automator.WaitUntilTextAsync(
                    AppMessages.WorkspaceSectionUnstagedForLocale(locale),
                    TimeSpan.FromSeconds(3));
                await automator.KeyAsync(Hex1bKey.F, Hex1bModifiers.Control, timeout.Token);
                await automator.WaitUntilTextAsync(
                    $"{AppMessages.DiffActionTextForLocale(locale)}:",
                    TimeSpan.FromSeconds(3));
                using var snapshot = automator.CreateSnapshot();
                Assert.IsTrue(snapshot.ContainsText(AppMessages.DiffActionPreviousShortForLocale(locale)));
                Assert.IsTrue(snapshot.ContainsText(AppMessages.DiffActionNextForLocale(locale)));
                Assert.IsTrue(snapshot.ContainsText(AppMessages.DiffActionHideForLocale(locale)));
                Assert.IsFalse(snapshot.ContainsText("Text:"));

                for (var row = 0; row < snapshot.Height; row++)
                {
                    Assert.IsLessThanOrEqualTo(
                        snapshot.Width,
                        DisplayWidth.GetStringWidth(snapshot.GetLine(row).TrimEnd()),
                        $"Locale '{locale}' overflowed terminal row {row} at 80x24.");
                }
            }
            finally
            {
                application?.RequestStop();
                await runTask;
                view.Detach();
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    /// <summary>
    /// Verifies F1 opens a wrapped Japanese keyboard reference without leaking English controls.
    /// </summary>
    [TestMethod]
    public async Task WorkspaceHelp_WithJapaneseLocale_RendersLocalizedKeyboardReference()
    {
        const string locale = "ja-JP";
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        var culture = CultureInfo.GetCultureInfo(locale);
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        var session = new FakeRepositoryWorkspaceSession(
            FakeRepositoryWorkspaceSession.CreateUnstagedEntry("localized.txt"));
        var view = new RepositoryWorkspaceView(
            new GitSailShellOptions(ApplicationMode.Gui, WorkingDirectory: null),
            session,
            CancellationToken.None);
        Hex1bApp? application = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        try
        {
            await using var terminal = Hex1bTerminal.CreateBuilder()
                .WithHeadless()
                .WithDimensions(80, 24)
                .WithHex1bApp(
                    terminalOptions => terminalOptions.EnableMouse = true,
                    createdApplication =>
                    {
                        application = createdApplication;
                        view.Attach(createdApplication);
                        return view.Build;
                    })
                .Build();
            var runTask = terminal.RunAsync(timeout.Token);
            var automator = new Hex1bTerminalAutomator(terminal, TimeSpan.FromSeconds(3));

            try
            {
                await automator.KeyAsync(Hex1bKey.F1, timeout.Token);
                await automator.WaitUntilTextAsync(
                    AppMessages.HelpTitleForLocale(locale),
                    TimeSpan.FromSeconds(3));
                using var snapshot = automator.CreateSnapshot();
                Assert.IsTrue(snapshot.ContainsText(AppMessages.CommonActionCloseForLocale(locale)));
                Assert.IsTrue(snapshot.ContainsText(AppMessages.CommonActionDoctorForLocale(locale)));
                Assert.IsTrue(snapshot.ContainsText(AppMessages.HelpKeysPrimaryForLocale(locale)));
                Assert.IsTrue(snapshot.ContainsText(AppMessages.HelpKeysRegionsForLocale(locale)));
                Assert.IsFalse(snapshot.ContainsText("Help and keyboard reference"));

                for (var row = 0; row < snapshot.Height; row++)
                {
                    Assert.IsLessThanOrEqualTo(
                        snapshot.Width,
                        DisplayWidth.GetStringWidth(snapshot.GetLine(row).TrimEnd()),
                        $"Locale '{locale}' overflowed terminal row {row} in F1 help.");
                }
            }
            finally
            {
                application?.RequestStop();
                await runTask;
                view.Detach();
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }
}
