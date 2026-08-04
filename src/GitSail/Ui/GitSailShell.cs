using GitSail.CommandLine;
using Hex1b;
using Hex1b.Input;

namespace GitSail.Ui;

/// <summary>
/// Runs the interactive terminal shell for a selected application mode.
/// </summary>
/// <param name="mode">The application mode selected by the command line.</param>
internal sealed class GitSailShell(ApplicationMode mode)
{
    private readonly ApplicationMode _mode = mode;

    /// <summary>
    /// Runs the terminal UI until the user exits or cancellation is requested.
    /// </summary>
    /// <param name="cancellationToken">Signals graceful terminal shutdown.</param>
    /// <returns>A task that completes after terminal state has been restored.</returns>
    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        var detail = "F1 Help  F2 Commands  F10 Menu  Ctrl+Q Quit";
        Hex1bApp? application = null;
        application = new Hex1bApp(context =>
            context.VStack(builder =>
            [
                builder.Text("GitSail"),
                builder.Text($"Mode: {_mode.ToString().ToLowerInvariant()}"),
                builder.Text("Keyboard-first Git workflows in your terminal."),
                builder.Text(string.Empty),
                builder.Text(detail).Wrap(),
                builder.Text(string.Empty),
                builder.Button("Quit").OnClick(eventArgs => eventArgs.Context.RequestStop()),
            ]).InputBindings(bindings =>
            {
                bindings.Key(Hex1bKey.F1).Action(() =>
                {
                    detail = "Help: use F2 to discover commands; Ctrl+Q exits safely.";
                    application?.Invalidate();
                }, "Open help");
                bindings.Key(Hex1bKey.F2).Action(() =>
                {
                    detail = "Commands: Repository Edit View Branch Commit Merge Remote Stash History Tools Help";
                    application?.Invalidate();
                }, "Open command palette");
                bindings.Key(Hex1bKey.F10).Action(() =>
                {
                    detail = "Menu: Repository | Edit | View | Branch | Commit | Merge | Remote | Stash | History | Tools | Help";
                    application?.Invalidate();
                }, "Open menu");
                bindings.Ctrl().Key(Hex1bKey.Q).Action(context => context.RequestStop(), "Quit GitSail");
            }),
            new Hex1bAppOptions
            {
                EnableMouse = true,
                EnableDefaultCtrlCExit = true,
            });

        using (application)
        {
            await application.RunAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
