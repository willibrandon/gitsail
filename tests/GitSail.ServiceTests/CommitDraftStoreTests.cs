using GitSail.Domain;
using GitSail.Git.Execution;
using System.Diagnostics;
using System.Text;

namespace GitSail.ServiceTests;

/// <summary>
/// Verifies debounced atomic commit-draft persistence, backup rotation, and identity-safe discard.
/// </summary>
[TestClass]
public sealed class CommitDraftStoreTests
{
    private string? _temporaryDirectory;

    /// <summary>
    /// Creates one isolated native-path directory for each draft-persistence test.
    /// </summary>
    [TestInitialize]
    public void Initialize()
    {
        _temporaryDirectory = Path.Combine(Path.GetTempPath(), $"gitsail-draft-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_temporaryDirectory);
    }

    /// <summary>
    /// Removes the isolated draft-persistence directory after each test.
    /// </summary>
    [TestCleanup]
    public void Cleanup()
    {
        if (_temporaryDirectory is not null && Directory.Exists(_temporaryDirectory))
        {
            TestDirectory.Delete(_temporaryDirectory);
        }
    }

    /// <summary>
    /// Verifies the idle timer writes the newest complete draft without an explicit flush.
    /// </summary>
    [TestMethod]
    public async Task ScheduleSave_AfterIdleDelay_PersistsLatestDraft()
    {
        var messagePath = CreatePath("GITGUI_MSG");
        var backupPath = CreatePath("GITGUI_BCK");
        await using var store = new CommitDraftStore(
            messagePath,
            backupPath,
            initialMessage: string.Empty,
            TimeSpan.FromMilliseconds(10));

        store.ScheduleSave("first");
        store.ScheduleSave("latest\n");
        var actual = await WaitForContentsAsync(messagePath, "latest\n"u8.ToArray());

        CollectionAssert.AreEqual("latest\n"u8.ToArray(), actual);
        Assert.IsNull(await RepositoryStateFileSystem.ReadIfExistsAsync(
            backupPath,
            maximumBytes: 1024,
            TestContext.Current!.CancellationToken));
    }

    /// <summary>
    /// Verifies explicit flushing rotates the previous complete primary draft into the backup file.
    /// </summary>
    [TestMethod]
    public async Task FlushAsync_AfterSecondRevision_PreservesPreviousRevisionAsBackup()
    {
        var messagePath = CreatePath("GITGUI_MSG");
        var backupPath = CreatePath("GITGUI_BCK");
        await using var store = new CommitDraftStore(
            messagePath,
            backupPath,
            initialMessage: string.Empty,
            TimeSpan.FromHours(1));

        store.ScheduleSave("first\n");
        await store.FlushAsync(TestContext.Current!.CancellationToken);
        store.ScheduleSave("second\n");
        await store.FlushAsync(TestContext.Current.CancellationToken);

        CollectionAssert.AreEqual(
            "second\n"u8.ToArray(),
            await RepositoryStateFileSystem.ReadIfExistsAsync(
                messagePath,
                maximumBytes: 1024,
                TestContext.Current.CancellationToken));
        CollectionAssert.AreEqual(
            "first\n"u8.ToArray(),
            await RepositoryStateFileSystem.ReadIfExistsAsync(
                backupPath,
                maximumBytes: 1024,
                TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Verifies discard rejects a stale version and deletes both files for the matching version.
    /// </summary>
    [TestMethod]
    public async Task TryDiscardAsync_WithVersionPrecondition_DeletesOnlyMatchingRecoveryState()
    {
        var messagePath = CreatePath("GITGUI_MSG");
        var backupPath = CreatePath("GITGUI_BCK");
        await using var store = new CommitDraftStore(
            messagePath,
            backupPath,
            initialMessage: string.Empty,
            TimeSpan.FromHours(1));
        store.ScheduleSave("first\n");
        await store.FlushAsync(TestContext.Current!.CancellationToken);
        store.ScheduleSave("second\n");
        await store.FlushAsync(TestContext.Current.CancellationToken);
        var currentVersion = store.Version;

        Assert.IsFalse(await store.TryDiscardAsync(
            currentVersion - 1,
            TestContext.Current.CancellationToken));
        Assert.IsNotNull(await RepositoryStateFileSystem.ReadIfExistsAsync(
            messagePath,
            maximumBytes: 1024,
            TestContext.Current.CancellationToken));
        Assert.IsTrue(await store.TryDiscardAsync(
            currentVersion,
            TestContext.Current.CancellationToken));

        Assert.IsNull(await RepositoryStateFileSystem.ReadIfExistsAsync(
            messagePath,
            maximumBytes: 1024,
            TestContext.Current.CancellationToken));
        Assert.IsNull(await RepositoryStateFileSystem.ReadIfExistsAsync(
            backupPath,
            maximumBytes: 1024,
            TestContext.Current.CancellationToken));
    }

    private GitPath CreatePath(string fileName)
    {
        var path = Path.Combine(_temporaryDirectory!, fileName);
        return OperatingSystem.IsWindows()
            ? GitPath.FromWindowsPath(path)
            : GitPath.FromUnixBytes(Encoding.UTF8.GetBytes(path));
    }

    private static async Task<byte[]> WaitForContentsAsync(GitPath path, byte[] expected)
    {
        var timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < TimeSpan.FromSeconds(3))
        {
            var actual = await RepositoryStateFileSystem.ReadIfExistsAsync(
                path,
                maximumBytes: 1024,
                TestContext.Current!.CancellationToken);
            if (actual is not null && actual.AsSpan().SequenceEqual(expected))
            {
                return actual;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10), TestContext.Current.CancellationToken);
        }

        Assert.Fail("The debounced commit draft was not persisted before the test timeout.");
        return [];
    }
}
