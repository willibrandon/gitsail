namespace GitSail.Features.Help;

/// <summary>
/// Writes the embedded operational guidance that supplements generated command help.
/// </summary>
internal static class OfflineManualRenderer
{
    /// <summary>
    /// Writes the complete offline operational manual to one invocation-owned writer.
    /// </summary>
    /// <param name="output">The destination selected by the command invocation.</param>
    internal static void Write(TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);
        output.WriteLine();
        output.WriteLine("Offline manual:");
        output.WriteLine();
        output.WriteLine("Installation and invocation");
        output.WriteLine("  Install:   dotnet tool install --global GitSail");
        output.WriteLine("  Update:    dotnet tool update --global GitSail");
        output.WriteLine("  Uninstall: dotnet tool uninstall --global GitSail");
        output.WriteLine("  Run as git-tui, git tui, or dotnet tool run git-tui from a local manifest.");
        output.WriteLine("  Run 'git-tui doctor' to check Git, the .NET SDK, PATH, terminal, locale, and storage.");
        output.WriteLine();
        output.WriteLine("Everyday controls");
        output.WriteLine("  Tab and Shift+Tab move focus. Arrow keys move within the focused control.");
        output.WriteLine("  Enter or a primary mouse click activates the focused item. Escape closes the top popup.");
        output.WriteLine("  F1 opens context help. F2 opens all commands. F5 refreshes. F7 filters changed paths. Ctrl+Q quits.");
        output.WriteLine("  Ctrl+F searches the current diff. F3 and Shift+F3 move between matches.");
        output.WriteLine("  S stages, U unstages, A stages all, and Shift+U unstages all in the commit workspace.");
        output.WriteLine("  Mouse selection, activation, wheel scrolling, editor selection, and window resizing are supported.");
        output.WriteLine();
        output.WriteLine("Repository safety");
        output.WriteLine("  GitSail delegates repository transactions, locks, hooks, signing, filters, and refs to Git.");
        output.WriteLine("  Outside worktree and Git changes refresh automatically; F5 requests an immediate full refresh.");
        output.WriteLine("  Destructive actions show the exact target and default to cancel.");
        output.WriteLine("  Commit, merge, rebase, stash, branch, remote, and worktree actions recheck displayed state before mutation.");
        output.WriteLine("  Repository paths and Git output are displayed with control characters made visible.");
        output.WriteLine("  NUL-delimited --pathspec-from-file input is the safe automation route for unusual native paths.");
        output.WriteLine();
        output.WriteLine("Configuration and tools");
        output.WriteLine("  Git configuration precedence remains system, global, local, worktree, then command scope.");
        output.WriteLine("  Conditional includes and repository trust are resolved by Git. Doctor lists sources but never values.");
        output.WriteLine("  Executables are resolved only from absolute PATH entries and are rechecked before use.");
        output.WriteLine();
        output.WriteLine("Terminal and accessibility");
        output.WriteLine("  GitSail requires attached standard input and output for the TUI and restores terminal modes on exit.");
        output.WriteLine("  The layout adapts down to 60x18. A resize notice replaces the workspace below that size.");
        output.WriteLine("  Press F10 for the complete application menu or F2 to search the same live actions.");
        output.WriteLine("  Color is not the only status signal. Keyboard access and stable text labels remain available.");
        output.WriteLine("  gitsail.clipboard selects off, auto, osc52, or helper. Helper success is confirmed; OSC 52 acceptance is not.");
        output.WriteLine();
        output.WriteLine("Diagnostics");
        output.WriteLine("  Run 'git-tui doctor --json' for stable automation output.");
        output.WriteLine("  Add '--trace' to an interactive command for a bounded private trace, or '--trace=<file>' to select it.");
        output.WriteLine("  Open 'Trace log' from F2 Commands while tracing to inspect sanitized events. Traces omit patches, messages, prompts, and environment blocks.");
        output.WriteLine("  Match crash symbols to the GitSail version and runtime identifier shown by Doctor and the release build-ID manifest.");
        output.WriteLine();
        output.WriteLine("Command details");
        output.WriteLine("  Run 'git-tui help <command>' or 'git-tui <command> --help' for generated syntax and options.");
    }
}
