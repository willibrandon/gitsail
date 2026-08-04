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
    /// <param name="rootCommand">The command model used to produce completions.</param>
    /// <param name="shell">The target shell name.</param>
    /// <param name="writer">The destination writer.</param>
    internal static void Write(RootCommand rootCommand, string shell, TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(rootCommand);
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(writer);

        switch (shell)
        {
            case "bash":
                writer.WriteLine("# Install: git-tui completion bash > ~/.local/share/bash-completion/completions/git-tui");
                writer.WriteLine("_git_tui_complete() {");
                writer.WriteLine("  local candidate");
                writer.WriteLine("  local index");
                writer.WriteLine("  local -a words=()");
                writer.WriteLine("  for ((index = 1; index < ${#COMP_WORDS[@]}; index++)); do");
                writer.WriteLine("    words+=(\"${COMP_WORDS[index]}\")");
                writer.WriteLine("  done");
                writer.WriteLine("  COMPREPLY=()");
                writer.WriteLine("  while IFS= read -r candidate; do");
                writer.WriteLine("    COMPREPLY+=(\"$candidate\")");
                writer.WriteLine("  done < <(command git-tui completion-candidates -- \"${words[@]}\")");
                writer.WriteLine("}");
                writer.WriteLine("complete -o default -F _git_tui_complete git-tui");
                break;
            case "zsh":
                writer.WriteLine("#compdef git-tui");
                writer.WriteLine("# Install: git-tui completion zsh > \"${fpath[1]}/_git-tui\"");
                writer.WriteLine("_git_tui_complete() {");
                writer.WriteLine("  local -a candidates");
                writer.WriteLine("  candidates=(\"${(@f)$(command git-tui completion-candidates -- \"${words[2,-1]}\")}\")");
                writer.WriteLine("  _describe 'GitSail value' candidates");
                writer.WriteLine("}");
                writer.WriteLine("compdef _git_tui_complete git-tui");
                break;
            case "fish":
                writer.WriteLine("# Install: git-tui completion fish > ~/.config/fish/completions/git-tui.fish");
                writer.WriteLine("function __git_tui_candidates");
                writer.WriteLine("    set -l words (commandline -opc)");
                writer.WriteLine("    set -e words[1]");
                writer.WriteLine("    command git-tui completion-candidates -- $words (commandline -ct)");
                writer.WriteLine("end");
                writer.WriteLine("complete -c git-tui -f -a '(__git_tui_candidates)'");
                break;
            case "powershell":
                writer.WriteLine("# Install: add `git-tui completion powershell | Out-String | Invoke-Expression` to $PROFILE");
                writer.WriteLine("Register-ArgumentCompleter -Native -CommandName git-tui -ScriptBlock {");
                writer.WriteLine("  param($wordToComplete, $commandAst, $cursorPosition)");
                writer.WriteLine("  $words = @($commandAst.CommandElements | Select-Object -Skip 1 | ForEach-Object { $_.Extent.Text })");
                writer.WriteLine("  if ($wordToComplete -eq '') { $words += '' }");
                writer.WriteLine("  & git-tui completion-candidates -- @words |");
                writer.WriteLine("    Where-Object { $_ -like \"$wordToComplete*\" } |");
                writer.WriteLine("    ForEach-Object { [System.Management.Automation.CompletionResult]::new($_, $_, 'ParameterValue', $_) }");
                writer.WriteLine("}");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(shell), shell, "Unsupported completion shell.");
        }
    }
}
