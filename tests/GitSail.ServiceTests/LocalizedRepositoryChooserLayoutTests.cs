using GitSail.Git.Execution;
using GitSail.Localization.Generated;
using GitSail.Ui;
using Hex1b;
using Hex1b.Automation;
using Hex1b.Input;
using System.Globalization;

namespace GitSail.ServiceTests;

/// <summary>
/// Verifies localized repository chooser chrome and minimum-size guidance against real Git discovery.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class LocalizedRepositoryChooserLayoutTests
{
    /// <summary>
    /// Verifies Japanese chooser navigation, field labels, status, and shortcuts fit at eighty columns.
    /// </summary>
    [TestMethod]
    public async Task Chooser_WithJapaneseLocale_FitsEightyByTwentyFour()
    {
        const string locale = "ja-JP";
        var temporaryDirectory = CreateTemporaryDirectory();
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        var culture = CultureInfo.GetCultureInfo(locale);
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;

        try
        {
            var processEnvironment = CreateProcessEnvironment(temporaryDirectory);
            using var session = await RepositoryChooserSession.CreateAsync(
                CanonicalDirectory.Create(temporaryDirectory),
                processEnvironment,
                "リポジトリを選択してください。",
                TestContext.Current!.CancellationToken);
            var view = new RepositoryChooserView(session, TestContext.Current.CancellationToken);
            Hex1bApp? application = null;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            await using var terminal = Hex1bTerminal.CreateBuilder()
                .WithHeadless()
                .WithDimensions(80, 24)
                .WithHex1bApp(
                    options => options.EnableMouse = true,
                    createdApplication =>
                    {
                        application = createdApplication;
                        view.Attach(createdApplication);
                        return view.Build;
                    })
                .Build();
            var runTask = terminal.RunAsync(timeout.Token);
            var automator = new Hex1bTerminalAutomator(terminal, TimeSpan.FromSeconds(10));

            try
            {
                await automator.WaitUntilTextAsync(
                    AppMessages.ChooserHeaderTitleForLocale(locale),
                    TimeSpan.FromSeconds(10));
                using var snapshot = automator.CreateSnapshot();
                Assert.IsTrue(snapshot.ContainsText(
                    AppMessages.ChooserSectionRepositoryActionsForLocale(locale)));
                Assert.IsTrue(snapshot.ContainsText(
                    $"[{AppMessages.ChooserActionOpenForLocale(locale)}]"));
                Assert.IsTrue(snapshot.ContainsText(
                    AppMessages.ChooserActionOpenWorktreeForLocale(locale)));
                Assert.IsTrue(snapshot.ContainsText(
                    AppMessages.ChooserLabelDirectoryForLocale(locale)));
                Assert.IsTrue(snapshot.ContainsText(
                    AppMessages.ChooserLabelStatusForLocale(locale)));
                Assert.IsTrue(snapshot.ContainsText("適用します。"));
                Assert.IsTrue(snapshot.ContainsText(
                    $"F1 {AppMessages.WorkspaceActionHelpForLocale(locale)}"));
                Assert.IsFalse(snapshot.ContainsText(
                    AppMessages.WorkspaceResizeTitleForLocale(locale)));

                AssertRowsFit(snapshot, locale, 80, 24);

                await automator.KeyAsync(Hex1bKey.F1, timeout.Token);
                await automator.WaitUntilTextAsync(
                    AppMessages.ChooserHelpTitleForLocale(locale),
                    TimeSpan.FromSeconds(10));
                using var help = automator.CreateSnapshot();
                Assert.IsTrue(help.ContainsText(
                    AppMessages.ChooserHelpOpenForLocale(locale).Substring(0, 15)));
                Assert.IsTrue(help.ContainsText(AppMessages.CommonActionCloseForLocale(locale)));
                Assert.IsFalse(help.ContainsText("Repository chooser help"));
                AssertRowsFit(help, locale, 80, 24);
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
            TestDirectory.Delete(temporaryDirectory);
        }
    }

    /// <summary>
    /// Verifies the Japanese minimum-size chooser replaces ordinary actions with safe localized controls.
    /// </summary>
    [TestMethod]
    public async Task Chooser_WithJapaneseLocale_RendersDedicatedMinimumSizeLayout()
    {
        const string locale = "ja-JP";
        var temporaryDirectory = CreateTemporaryDirectory();
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        var culture = CultureInfo.GetCultureInfo(locale);
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;

        try
        {
            var processEnvironment = CreateProcessEnvironment(temporaryDirectory);
            using var session = await RepositoryChooserSession.CreateAsync(
                CanonicalDirectory.Create(temporaryDirectory),
                processEnvironment,
                "リポジトリを選択してください。",
                TestContext.Current!.CancellationToken);
            var view = new RepositoryChooserView(session, TestContext.Current.CancellationToken);
            Hex1bApp? application = null;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            await using var terminal = Hex1bTerminal.CreateBuilder()
                .WithHeadless()
                .WithDimensions(59, 17)
                .WithHex1bApp(
                    options => options.EnableMouse = true,
                    createdApplication =>
                    {
                        application = createdApplication;
                        view.Attach(createdApplication);
                        return view.Build;
                    })
                .Build();
            var runTask = terminal.RunAsync(timeout.Token);
            var automator = new Hex1bTerminalAutomator(terminal, TimeSpan.FromSeconds(10));

            try
            {
                await automator.WaitUntilTextAsync(
                    AppMessages.WorkspaceResizeTitleForLocale(locale),
                    TimeSpan.FromSeconds(10));
                using var snapshot = automator.CreateSnapshot();
                Assert.IsTrue(snapshot.ContainsText("GitSail には幅 60 列、高さ 18"));
                Assert.IsTrue(snapshot.ContainsText("ターミナルのサイズを変更してリポジトリ選択"));
                Assert.IsTrue(snapshot.ContainsText("戻ってくださ"));
                Assert.IsTrue(snapshot.ContainsText("い。"));
                Assert.IsTrue(snapshot.ContainsText(
                    $"F5 {AppMessages.ChooserActionRecentForLocale(locale)}"));
                Assert.IsTrue(snapshot.ContainsText(
                    $"Ctrl+Q {AppMessages.WorkspaceActionQuitForLocale(locale)}"));
                Assert.IsFalse(snapshot.ContainsText(
                    AppMessages.ChooserSectionRepositoryActionsForLocale(locale)));

                AssertRowsFit(snapshot, locale, 59, 17);
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
            TestDirectory.Delete(temporaryDirectory);
        }
    }

    private static void AssertRowsFit(
        Hex1bTerminalSnapshot snapshot,
        string locale,
        int width,
        int height)
    {
        for (var row = 0; row < snapshot.Height; row++)
        {
            Assert.IsLessThanOrEqualTo(
                snapshot.Width,
                DisplayWidth.GetStringWidth(snapshot.GetLine(row).TrimEnd()),
                $"Locale '{locale}' overflowed terminal row {row} at {width}x{height}.");
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gitsail-localized-chooser-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static TestProcessEnvironment CreateProcessEnvironment(string homeDirectory)
        => new(new Dictionary<string, string?>
        {
            ["HOME"] = homeDirectory,
            ["USERPROFILE"] = homeDirectory,
            ["XDG_CONFIG_HOME"] = Path.Combine(homeDirectory, "xdg-config"),
            ["GIT_CONFIG_NOSYSTEM"] = "1",
            ["PATH"] = Environment.GetEnvironmentVariable("PATH"),
            ["SystemRoot"] = Environment.GetEnvironmentVariable("SystemRoot"),
            ["WINDIR"] = Environment.GetEnvironmentVariable("WINDIR"),
        });
}
