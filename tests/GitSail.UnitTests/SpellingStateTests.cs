using GitSail.Domain;
using GitSail.Ui;
using Hex1b.Documents;
using System.Collections.Immutable;

namespace GitSail.UnitTests;

/// <summary>
/// Verifies version-matched spelling state, editor decoration, and safe suggestion replacement.
/// </summary>
[TestClass]
public sealed class SpellingStateTests
{
    private static readonly ImmutableArray<string> s_suggestions = ["the", "tech"];

    /// <summary>
    /// Verifies a current result produces one exact theme-aware editor range.
    /// </summary>
    [TestMethod]
    public void TryComplete_WithCurrentDocumentVersion_ProducesExactDecoration()
    {
        var spelling = new SpellingState();
        var message = new CommitMessageState("Fix teh parser", spelling: spelling);
        spelling.BeginCheck(message.Version);

        var accepted = spelling.TryComplete(CreateResult(message.Version));
        var spans = spelling.DecorationProvider.GetDecorations(
            1,
            1,
            message.Editor.Document);

        Assert.IsTrue(accepted);
        Assert.HasCount(1, spans);
        Assert.AreEqual(new DocumentPosition(1, 5), spans[0].Start);
        Assert.AreEqual(new DocumentPosition(1, 8), spans[0].End);
        Assert.HasCount(2, spelling.Issues[0].Suggestions);
        StringAssert.Contains(spelling.StatusText, "1 possible misspelling", StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies a superseded checker result cannot decorate a newer editor version.
    /// </summary>
    [TestMethod]
    public void TryComplete_WithSupersededDocumentVersion_RejectsStaleIssues()
    {
        var spelling = new SpellingState();
        spelling.BeginCheck(documentVersion: 8);

        var accepted = spelling.TryComplete(CreateResult(documentVersion: 7));

        Assert.IsFalse(accepted);
        Assert.IsEmpty(spelling.Issues);
        Assert.IsTrue(spelling.IsChecking);
    }

    /// <summary>
    /// Verifies only an advertised suggestion replaces still-matching misspelled text.
    /// </summary>
    [TestMethod]
    public void TryReplaceSpellingIssue_WithAdvertisedSuggestion_ReplacesExactWord()
    {
        var spelling = new SpellingState();
        var message = new CommitMessageState("Fix teh parser", spelling: spelling);
        spelling.BeginCheck(message.Version);
        _ = spelling.TryComplete(CreateResult(message.Version));
        var issue = spelling.Issues[0];

        var rejected = message.TryReplaceSpellingIssue(issue, "there");
        var replaced = message.TryReplaceSpellingIssue(issue, "the");

        Assert.IsFalse(rejected);
        Assert.IsTrue(replaced);
        Assert.AreEqual("Fix the parser", message.Message);
        Assert.AreEqual(7, message.Editor.Cursor.Position.Value);
        Assert.IsEmpty(spelling.Issues);
    }

    private static SpellCheckResult CreateResult(long documentVersion)
        => new(
            documentVersion,
            "en_US",
            "Aspell 0.60.8",
            [new SpellingIssue(4, 3, "teh", s_suggestions)]);
}
