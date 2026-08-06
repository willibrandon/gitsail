using System.Collections.Immutable;

namespace GitSail.Domain;

/// <summary>
/// Contains the exact selected repository values exposed to one configured tool invocation.
/// </summary>
internal sealed class ConfiguredToolInvocation
{
    private const int MaximumSelectedPaths = 4096;
    private const int MaximumPromptCharacters = 64 * 1024;

    /// <summary>
    /// Initializes one bounded immutable configured-tool input snapshot.
    /// </summary>
    /// <param name="focusedPath">The exact focused path, when one is selected.</param>
    /// <param name="selectedPaths">The exact ordered selected paths.</param>
    /// <param name="currentBranch">The exact current branch name, or none for detached HEAD.</param>
    /// <param name="arguments">The optional user-entered tool arguments.</param>
    /// <param name="revision">The optional user-entered revision.</param>
    internal ConfiguredToolInvocation(
        GitPath? focusedPath,
        ImmutableArray<GitPath> selectedPaths,
        RefName? currentBranch,
        string? arguments,
        string? revision)
    {
        if (selectedPaths.IsDefault || selectedPaths.Length > MaximumSelectedPaths ||
            selectedPaths.Any(static path => path is null))
        {
            throw new ArgumentException(
                "Configured-tool selected paths must be initialized, bounded, and non-null.",
                nameof(selectedPaths));
        }

        ValidatePromptValue(arguments, nameof(arguments));
        ValidatePromptValue(revision, nameof(revision));
        FocusedPath = focusedPath;
        SelectedPaths = selectedPaths;
        CurrentBranch = currentBranch;
        Arguments = arguments;
        Revision = revision;
    }

    /// <summary>
    /// Gets the exact focused path, when one is selected.
    /// </summary>
    internal GitPath? FocusedPath { get; }

    /// <summary>
    /// Gets the exact ordered selected paths.
    /// </summary>
    internal ImmutableArray<GitPath> SelectedPaths { get; }

    /// <summary>
    /// Gets the exact current branch name, or none for detached HEAD.
    /// </summary>
    internal RefName? CurrentBranch { get; }

    /// <summary>
    /// Gets the optional user-entered tool arguments.
    /// </summary>
    internal string? Arguments { get; }

    /// <summary>
    /// Gets the optional user-entered revision.
    /// </summary>
    internal string? Revision { get; }

    private static void ValidatePromptValue(string? value, string parameterName)
    {
        if (value is not null &&
            (value.Length > MaximumPromptCharacters || value.Contains('\0', StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "Configured-tool prompt input is too long or contains NUL.",
                parameterName);
        }
    }
}
