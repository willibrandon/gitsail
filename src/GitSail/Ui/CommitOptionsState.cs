using GitSail.Domain;
using Hex1b.Widgets;

namespace GitSail.Ui;

/// <summary>
/// Owns lifted commit-transaction options and their editable author and signing-key inputs.
/// </summary>
internal sealed class CommitOptionsState
{
    /// <summary>
    /// Initializes commit options with an optional command-line amend request.
    /// </summary>
    /// <param name="amend">Whether amend mode is initially active.</param>
    internal CommitOptionsState(bool amend)
    {
        Amend = amend;
        Author = new TextBoxState();
        SigningKey = new TextBoxState();
    }

    /// <summary>
    /// Gets whether the expanded option controls are currently visible.
    /// </summary>
    internal bool IsExpanded { get; private set; }

    /// <summary>
    /// Gets whether the current HEAD commit will be amended.
    /// </summary>
    internal bool Amend { get; private set; }

    /// <summary>
    /// Gets whether Git will append the committer signoff trailer.
    /// </summary>
    internal bool Signoff { get; private set; }

    /// <summary>
    /// Gets whether Git will sign the resulting commit.
    /// </summary>
    internal bool SignCommit { get; private set; }

    /// <summary>
    /// Gets the selected Git commit-message cleanup behavior.
    /// </summary>
    internal CommitCleanupMode CleanupMode { get; private set; }

    /// <summary>
    /// Gets the lifted explicit-author input state.
    /// </summary>
    internal TextBoxState Author { get; }

    /// <summary>
    /// Gets the lifted optional signing-key input state.
    /// </summary>
    internal TextBoxState SigningKey { get; }

    /// <summary>
    /// Expands or collapses the complete commit-option controls.
    /// </summary>
    internal void ToggleExpanded()
        => IsExpanded = !IsExpanded;

    /// <summary>
    /// Enables or disables replacement of the current HEAD commit.
    /// </summary>
    internal void ToggleAmend()
        => Amend = !Amend;

    /// <summary>
    /// Enables or disables the committer signoff trailer.
    /// </summary>
    internal void ToggleSignoff()
        => Signoff = !Signoff;

    /// <summary>
    /// Enables or disables Git commit signing.
    /// </summary>
    internal void ToggleSignCommit()
        => SignCommit = !SignCommit;

    /// <summary>
    /// Advances through every documented cleanup mode in stable display order.
    /// </summary>
    internal void CycleCleanupMode()
        => CleanupMode = CleanupMode switch
        {
            CommitCleanupMode.Default => CommitCleanupMode.Strip,
            CommitCleanupMode.Strip => CommitCleanupMode.Whitespace,
            CommitCleanupMode.Whitespace => CommitCleanupMode.Verbatim,
            CommitCleanupMode.Verbatim => CommitCleanupMode.Scissors,
            CommitCleanupMode.Scissors => CommitCleanupMode.Default,
            _ => throw new InvalidOperationException("The commit cleanup mode is outside the supported set."),
        };

    /// <summary>
    /// Creates one immutable transaction request from the current lifted option values.
    /// </summary>
    /// <param name="message">The complete commit-editor message.</param>
    /// <param name="skipHooks">Whether the separately confirmed action bypasses applicable hooks.</param>
    /// <param name="confirmedPublishedAmendWarning">The exact current local publication warning that was confirmed.</param>
    /// <param name="confirmedDetachedHeadWarning">The exact current detached HEAD warning that was confirmed.</param>
    /// <returns>The controlled Git-owned commit transaction request.</returns>
    internal CommitRequest CreateRequest(
        string message,
        bool skipHooks = false,
        PublishedAmendWarning? confirmedPublishedAmendWarning = null,
        DetachedHeadWarning? confirmedDetachedHeadWarning = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        var author = NormalizeOptionalValue(Author.Text);
        var signingKey = NormalizeOptionalValue(SigningKey.Text);
        return new CommitRequest(
            message,
            Amend,
            Signoff,
            author,
            CleanupMode,
            skipHooks,
            SignCommit,
            signingKey,
            confirmedPublishedAmendWarning,
            confirmedDetachedHeadWarning);
    }

    private static string? NormalizeOptionalValue(string value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
