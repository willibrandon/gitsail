using System.CommandLine;

namespace GitSail.CommandLine;

/// <summary>
/// Generates shell completion scripts from the System.CommandLine command model.
/// </summary>
internal static class CompletionRenderer
{
    /// <summary>
    /// Writes a completion script for a supported shell.
    /// </summary>
    /// <param name="rootCommand">The authoritative command model.</param>
    /// <param name="shell">The target shell name.</param>
    /// <param name="writer">The destination writer.</param>
    internal static void Write(RootCommand rootCommand, string shell, TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(rootCommand);
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(writer);

        var commands = string.Join(' ', rootCommand.Subcommands.Select(static command => command.Name));
        switch (shell)
        {
            case "bash":
                writer.WriteLine("_git_tui_complete() {");
                writer.WriteLine($"  COMPREPLY=( $(compgen -W \"{commands}\" -- \"${{COMP_WORDS[COMP_CWORD]}}\") )");
                writer.WriteLine("}");
                writer.WriteLine("complete -F _git_tui_complete git-tui");
                break;
            case "zsh":
                writer.WriteLine("#compdef git-tui");
                writer.WriteLine($"_arguments '1:command:({commands})' '*::argument:->args'");
                break;
            case "fish":
                writer.WriteLine("complete -c git-tui -f");
                foreach (var command in rootCommand.Subcommands)
                {
                    writer.WriteLine($"complete -c git-tui -n '__fish_use_subcommand' -a '{command.Name}' -d '{command.Description}'");
                }

                break;
            case "powershell":
                writer.WriteLine("Register-ArgumentCompleter -Native -CommandName git-tui -ScriptBlock {");
                writer.WriteLine("  param($wordToComplete)");
                writer.WriteLine($"  '{commands}' -split ' ' | Where-Object {{ $_ -like \"$wordToComplete*\" }}");
                writer.WriteLine("}");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(shell), shell, "Unsupported completion shell.");
        }
    }
}
