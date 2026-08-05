using GitSail.CommandLine;
using GitSail.Localization.Generated;
using GitSail.Ui;
using Hex1b;
using Hex1b.Automation;
using System.Globalization;

namespace GitSail.UnitTests;

/// <summary>
/// Verifies localized workspace text remains readable at every supported responsive breakpoint.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class LocalizedWorkspaceLayoutTests
{
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
}
