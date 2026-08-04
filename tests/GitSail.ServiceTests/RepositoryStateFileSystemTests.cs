using GitSail.Domain;
using GitSail.Git.Execution;
using System.Text;

namespace GitSail.ServiceTests;

/// <summary>
/// Verifies bounded no-follow reads and durable atomic native-path replacement.
/// </summary>
[TestClass]
public sealed class RepositoryStateFileSystemTests
{
    private string? _temporaryDirectory;

    /// <summary>
    /// Creates one isolated directory for each native state-file test.
    /// </summary>
    [TestInitialize]
    public void Initialize()
    {
        _temporaryDirectory = Path.Combine(Path.GetTempPath(), $"gitsail-state-file-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_temporaryDirectory);
    }

    /// <summary>
    /// Removes the isolated state-file directory after each test.
    /// </summary>
    [TestCleanup]
    public void Cleanup()
    {
        if (_temporaryDirectory is not null && Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }
    }

    /// <summary>
    /// Verifies missing reads and repeated atomic replacement retain the exact latest bytes.
    /// </summary>
    [TestMethod]
    public async Task WriteAndReadAsync_WithRepeatedReplacement_ReturnsExactLatestBytes()
    {
        var pathText = Path.Combine(_temporaryDirectory!, "GITGUI_EDITMSG");
        var path = CreatePath(pathText);

        var missing = await RepositoryStateFileSystem.ReadIfExistsAsync(
            path,
            maximumBytes: 1024,
            TestContext.Current!.CancellationToken);
        await RepositoryStateFileSystem.WriteAtomicallyAsync(
            path,
            "first\n"u8.ToArray(),
            TestContext.Current.CancellationToken);
        await RepositoryStateFileSystem.WriteAtomicallyAsync(
            path,
            "second \0 exact\n"u8.ToArray(),
            TestContext.Current.CancellationToken);
        if (!OperatingSystem.IsWindows())
        {
            Assert.AreEqual(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(pathText));
        }

        var actual = await RepositoryStateFileSystem.ReadIfExistsAsync(
            path,
            maximumBytes: 1024,
            TestContext.Current.CancellationToken);

        Assert.IsNull(missing);
        CollectionAssert.AreEqual("second \0 exact\n"u8.ToArray(), actual);
        Assert.HasCount(1, Directory.GetFiles(_temporaryDirectory!));
        Assert.IsTrue(await RepositoryStateFileSystem.DeleteIfExistsAsync(
            path,
            TestContext.Current.CancellationToken));
        Assert.IsFalse(await RepositoryStateFileSystem.DeleteIfExistsAsync(
            path,
            TestContext.Current.CancellationToken));
        Assert.IsFalse(File.Exists(pathText));
    }

    /// <summary>
    /// Verifies the bounded reader rejects content larger than its caller-owned contract.
    /// </summary>
    [TestMethod]
    public async Task ReadIfExistsAsync_AboveMaximum_ThrowsInvalidDataException()
    {
        var path = CreatePath(Path.Combine(_temporaryDirectory!, "GITGUI_MSG"));
        await RepositoryStateFileSystem.WriteAtomicallyAsync(
            path,
            new byte[33],
            TestContext.Current!.CancellationToken);

        _ = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => RepositoryStateFileSystem.ReadIfExistsAsync(
            path,
            maximumBytes: 32,
            TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Verifies atomic replacement replaces a symlink entry without modifying its external target.
    /// </summary>
    [TestMethod]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
    public async Task WriteAtomicallyAsync_OverSymlink_ReplacesEntryWithoutFollowingTarget()
    {
        var externalPath = Path.Combine(_temporaryDirectory!, "external.txt");
        var statePath = Path.Combine(_temporaryDirectory!, "GITGUI_BCK");
        File.WriteAllText(externalPath, "external\n");
        File.CreateSymbolicLink(statePath, externalPath);

        _ = await Assert.ThrowsExactlyAsync<IOException>(() => RepositoryStateFileSystem.ReadIfExistsAsync(
            CreatePath(statePath),
            maximumBytes: 1024,
            TestContext.Current!.CancellationToken));
        _ = await Assert.ThrowsExactlyAsync<IOException>(() => RepositoryStateFileSystem.DeleteIfExistsAsync(
            CreatePath(statePath),
            TestContext.Current!.CancellationToken));

        await RepositoryStateFileSystem.WriteAtomicallyAsync(
            CreatePath(statePath),
            "draft\n"u8.ToArray(),
            TestContext.Current!.CancellationToken);

        Assert.IsNull(File.ResolveLinkTarget(statePath, returnFinalTarget: false));
        Assert.AreEqual("draft\n", File.ReadAllText(statePath));
        Assert.AreEqual("external\n", File.ReadAllText(externalPath));
    }

    /// <summary>
    /// Verifies Unix reads and replacement retain a path containing non-UTF-8 native bytes.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux)]
    public async Task WriteAndReadAsync_WithNonUtf8UnixPath_RetainsExactNativeName()
    {
        var parentBytes = Encoding.UTF8.GetBytes(_temporaryDirectory!);
        var fileName = new byte[] { (byte)'G', (byte)'I', (byte)'T', 0xff, (byte)'M', (byte)'S', (byte)'G' };
        var pathBytes = new byte[parentBytes.Length + 1 + fileName.Length];
        parentBytes.CopyTo(pathBytes, 0);
        pathBytes[parentBytes.Length] = (byte)'/';
        fileName.CopyTo(pathBytes, parentBytes.Length + 1);
        var path = GitPath.FromUnixBytes(pathBytes);

        await RepositoryStateFileSystem.WriteAtomicallyAsync(
            path,
            "native bytes\n"u8.ToArray(),
            TestContext.Current!.CancellationToken);
        var actual = await RepositoryStateFileSystem.ReadIfExistsAsync(
            path,
            maximumBytes: 1024,
            TestContext.Current.CancellationToken);

        CollectionAssert.AreEqual("native bytes\n"u8.ToArray(), actual);
    }

    private static GitPath CreatePath(string path)
        => OperatingSystem.IsWindows()
            ? GitPath.FromWindowsPath(path)
            : GitPath.FromUnixBytes(Encoding.UTF8.GetBytes(path));
}
