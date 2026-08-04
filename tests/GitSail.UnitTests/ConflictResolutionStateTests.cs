using GitSail.Domain;
using GitSail.Git.Parsing;
using GitSail.Ui;
using System.Text;

namespace GitSail.UnitTests;

/// <summary>
/// Verifies lifted conflict choices, cursor mapping, mode selection, and refresh retention.
/// </summary>
[TestClass]
public sealed class ConflictResolutionStateTests
{
    /// <summary>
    /// Verifies every marker line maps to its chunk while ordinary surrounding lines do not.
    /// </summary>
    [TestMethod]
    public void FindChunkAtLine_WithFinalMarkerWithoutNewline_MapsCompleteMarkerBlocks()
    {
        var document = CreateDocument(
            "prefix\n" +
            "<<<<<<< gitsail-ours-abc123\n" +
            "ours one\n" +
            "||||||| gitsail-base-abc123\n" +
            "base one\n" +
            "=======\n" +
            "theirs one\n" +
            ">>>>>>> gitsail-theirs-abc123\n" +
            "middle\n" +
            "<<<<<<< gitsail-ours-abc123\n" +
            "ours two\n" +
            "||||||| gitsail-base-abc123\n" +
            "base two\n" +
            "=======\n" +
            "theirs two\n" +
            ">>>>>>> gitsail-theirs-abc123");
        var state = new ConflictResolutionState();

        state.SetDocument(CreateEntry(), document, new OperationGeneration(4));

        Assert.AreEqual(-1, state.FindChunkAtLine(0));
        Assert.AreEqual(0, state.FindChunkAtLine(1));
        Assert.AreEqual(0, state.FindChunkAtLine(7));
        Assert.AreEqual(-1, state.FindChunkAtLine(8));
        Assert.AreEqual(1, state.FindChunkAtLine(9));
        Assert.AreEqual(1, state.FindChunkAtLine(15));
        Assert.AreEqual(9, state.GetStartLine(1));
    }

    /// <summary>
    /// Verifies explicit choices build exact marker-free content and drive unresolved navigation.
    /// </summary>
    [TestMethod]
    public void SetChoice_WithMultipleChunks_BuildsSelectedContentAndTracksCompletion()
    {
        var document = CreateDocument(
            "<<<<<<< gitsail-ours-abc123\n" +
            "ours one\n" +
            "||||||| gitsail-base-abc123\n" +
            "base one\n" +
            "=======\n" +
            "theirs one\n" +
            ">>>>>>> gitsail-theirs-abc123\n" +
            "shared\n" +
            "<<<<<<< gitsail-ours-abc123\n" +
            "ours two\n" +
            "||||||| gitsail-base-abc123\n" +
            "base two\n" +
            "=======\n" +
            "theirs two\n" +
            ">>>>>>> gitsail-theirs-abc123\n");
        var state = new ConflictResolutionState();
        state.SetDocument(CreateEntry(), document, new OperationGeneration(5));

        state.SetChoice(0, ConflictResolutionChoice.Ours);

        Assert.IsFalse(state.IsComplete);
        Assert.AreEqual(1, state.ResolvedChunkCount);
        Assert.AreEqual(1, state.FindNextUnresolvedChunk(0));
        state.SetChoice(1, ConflictResolutionChoice.Both);
        Assert.IsTrue(state.IsComplete);
        Assert.AreEqual(-1, state.FindNextUnresolvedChunk(1));
        Assert.AreEqual(
            "ours one\nshared\nours two\ntheirs two\n",
            Encoding.UTF8.GetString(state.BuildResolvedContent()));
    }

    /// <summary>
    /// Verifies a regular-file conflict can deliberately select either executable-bit result.
    /// </summary>
    [TestMethod]
    public void ToggleExecutable_WithOnlyRegularStages_SelectsExecutableResult()
    {
        var state = new ConflictResolutionState();
        state.SetDocument(CreateEntry(), CreateDocument(CreateSingleChunkText()), new OperationGeneration(6));

        state.ToggleExecutable();

        Assert.IsTrue(state.CanToggleExecutable);
        Assert.AreEqual(GitFileMode.ExecutableFile, state.ResultMode);
    }

    /// <summary>
    /// Verifies refresh retains choices only while path, stages, and chunk count remain identical.
    /// </summary>
    [TestMethod]
    public void SetDocument_AfterRefresh_RetainsOnlyStageCompatibleChoices()
    {
        var state = new ConflictResolutionState();
        var entry = CreateEntry();
        var document = CreateDocument(CreateSingleChunkText());
        state.SetDocument(entry, document, new OperationGeneration(7));
        state.SetChoice(0, ConflictResolutionChoice.Theirs);

        state.SetDocument(entry, document, new OperationGeneration(8));

        Assert.IsTrue(state.IsComplete);
        Assert.AreEqual("theirs\n", Encoding.UTF8.GetString(state.BuildResolvedContent()));
        var changedStages = entry with
        {
            ConflictStages = entry.ConflictStages! with
            {
                Theirs = new ConflictStage(
                    GitFileMode.RegularFile,
                    CreateObjectId("4444444444444444444444444444444444444444")),
            },
        };
        state.SetDocument(changedStages, document, new OperationGeneration(9));
        Assert.IsFalse(state.IsComplete);
        Assert.AreEqual(0, state.ResolvedChunkCount);
    }

    /// <summary>
    /// Verifies quick choices participate in normal editor undo and redo without losing chunk mapping.
    /// </summary>
    [TestMethod]
    public void SetChoice_WithEditorHistory_UndoesAndRedoesExactMarkerReplacement()
    {
        var state = new ConflictResolutionState();
        state.SetDocument(CreateEntry(), CreateDocument(CreateSingleChunkText()), new OperationGeneration(10));
        state.SetChoice(0, ConflictResolutionChoice.Ours);

        state.Editor!.Undo();

        Assert.IsFalse(state.IsComplete);
        Assert.AreEqual(0, state.FindChunkAtLine(0));
        state.Editor.Redo();
        Assert.IsTrue(state.IsComplete);
        Assert.AreEqual("ours\n", Encoding.UTF8.GetString(state.BuildResolvedContent()));
    }

    /// <summary>
    /// Verifies an ordinary manual edit before a marker block retains exact quick-choice navigation.
    /// </summary>
    [TestMethod]
    public void EditorInsert_BeforeConflict_AdjustsMappedChunkRange()
    {
        var state = new ConflictResolutionState();
        state.SetDocument(
            CreateEntry(),
            CreateDocument("prefix\n" + CreateSingleChunkText()),
            new OperationGeneration(11));
        state.Editor!.SetCursorPosition(new Hex1b.Documents.DocumentOffset(0));

        state.Editor.InsertText("manual\n");

        Assert.AreEqual(0, state.FindChunkAtLine(2));
        state.SetChoice(0, ConflictResolutionChoice.Theirs);
        Assert.AreEqual(
            "manual\nprefix\ntheirs\n",
            Encoding.UTF8.GetString(state.BuildResolvedContent()));
    }

    /// <summary>
    /// Verifies invalid UTF-8 cannot enter a text editor that would re-encode replacement bytes.
    /// </summary>
    [TestMethod]
    public void SetDocument_WithInvalidUtf8_RequiresExternalConflictHandling()
    {
        var bytes = Encoding.UTF8.GetBytes(CreateSingleChunkText());
        var oursOffset = Encoding.UTF8.GetByteCount("<<<<<<< gitsail-ours-abc123\n");
        bytes[oursOffset] = 0xFF;
        var document = ConflictMarkerParser.Parse(
            bytes,
            new ConflictMarkerSet(markerSize: 7, token: "abc123"));
        var state = new ConflictResolutionState();

        var exception = Assert.ThrowsExactly<InvalidDataException>(() =>
            state.SetDocument(CreateEntry(), document, new OperationGeneration(12)));

        StringAssert.Contains(exception.Message, "valid UTF-8");
        Assert.IsFalse(state.IsActive);
    }

    private static ConflictMergeDocument CreateDocument(string content)
        => ConflictMarkerParser.Parse(
            Encoding.UTF8.GetBytes(content),
            new ConflictMarkerSet(markerSize: 7, token: "abc123"));

    private static string CreateSingleChunkText()
        => "<<<<<<< gitsail-ours-abc123\n" +
            "ours\n" +
            "||||||| gitsail-base-abc123\n" +
            "base\n" +
            "=======\n" +
            "theirs\n" +
            ">>>>>>> gitsail-theirs-abc123\n";

    private static RepositoryStatusEntry CreateEntry()
    {
        var path = OperatingSystem.IsWindows()
            ? GitPath.FromWindowsPath("conflict.txt")
            : GitPath.FromUnixBytes("conflict.txt"u8);
        return new RepositoryStatusEntry(
            RepositoryStatusEntryKind.Unmerged,
            GitFileStatus.Unmerged,
            GitFileStatus.Unmerged,
            path,
            OriginalPath: null,
            SimilarityPercentage: null,
            IsSubmodule: false,
            new ConflictStages(
                new ConflictStage(
                    GitFileMode.RegularFile,
                    CreateObjectId("1111111111111111111111111111111111111111")),
                new ConflictStage(
                    GitFileMode.RegularFile,
                    CreateObjectId("2222222222222222222222222222222222222222")),
                new ConflictStage(
                    GitFileMode.RegularFile,
                    CreateObjectId("3333333333333333333333333333333333333333")),
                GitFileMode.RegularFile));
    }

    private static ObjectId CreateObjectId(string value)
    {
        Assert.IsTrue(ObjectId.TryParseHex(Encoding.ASCII.GetBytes(value), out var objectId));
        Assert.IsNotNull(objectId);
        return objectId;
    }
}
