using GitSail.Domain;
using GitSail.Git.Execution;
using System.Text;

namespace GitSail.ServiceTests;

/// <summary>
/// Verifies private keyed revert recovery persistence, validation, retention, and cleanup.
/// </summary>
[TestClass]
public sealed class RevertUndoStoreTests
{
    private string? _temporaryDirectory;
    private TestProcessEnvironment? _environment;

    /// <summary>
    /// Creates one isolated platform user-directory tree for each recovery-store test.
    /// </summary>
    [TestInitialize]
    public void Initialize()
    {
        _temporaryDirectory = Path.Combine(Path.GetTempPath(), $"gitsail-revert-undo-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_temporaryDirectory);
        _environment = new TestProcessEnvironment(new Dictionary<string, string?>
        {
            ["HOME"] = Path.Combine(_temporaryDirectory, "home"),
            ["XDG_CONFIG_HOME"] = Path.Combine(_temporaryDirectory, "xdg-config"),
            ["XDG_CACHE_HOME"] = Path.Combine(_temporaryDirectory, "xdg-cache"),
            ["APPDATA"] = Path.Combine(_temporaryDirectory, "roaming"),
            ["LOCALAPPDATA"] = Path.Combine(_temporaryDirectory, "local"),
        });
    }

    /// <summary>
    /// Removes the isolated user-directory tree after each recovery-store test.
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
    /// Verifies a checksummed record survives restart under a stable opaque repository filename.
    /// </summary>
    [TestMethod]
    public async Task SaveAndLoadAsync_WithValidState_RoundTripsAndUsesPrivateOpaquePaths()
    {
        var repository = CreateRepository("secret-repository-name");
        var store = await RevertUndoStore.CreateAsync(
            repository,
            _environment!,
            TimeProvider.System,
            TestContext.Current!.CancellationToken);
        var state = store.CreateState(
            "exact patch \0 bytes"u8,
            CreatePrecondition());

        await store.SaveAsync(state, TestContext.Current.CancellationToken);
        var recoveryPath = GetPath(store.RecoveryPath);
        var restartedStore = await RevertUndoStore.CreateAsync(
            repository,
            _environment!,
            TimeProvider.System,
            TestContext.Current.CancellationToken);
        var recovered = await restartedStore.LoadAsync(TestContext.Current.CancellationToken);

        Assert.IsNotNull(recovered);
        CollectionAssert.AreEqual(state.Patch.ToArray(), recovered.Patch.ToArray());
        Assert.AreEqual(
            state.Precondition.HeadObjectId,
            recovered.Precondition.HeadObjectId);
        Assert.AreEqual(
            state.Precondition.HeadName,
            recovered.Precondition.HeadName);
        CollectionAssert.AreEqual(
            state.Precondition.IndexFingerprint.ToArray(),
            recovered.Precondition.IndexFingerprint.ToArray());
        Assert.AreEqual(recoveryPath, GetPath(restartedStore.RecoveryPath));
        Assert.IsFalse(
            Path.GetFileName(recoveryPath).Contains("secret-repository-name", StringComparison.Ordinal));
        Assert.IsTrue(File.Exists(recoveryPath));
        var directoryService = new UserDirectoryPathService(_environment!);
        var keyPath = Path.Combine(directoryService.GetConfigurationDirectory(), "repository-id.key");
        Assert.IsTrue(File.Exists(keyPath));
        if (!OperatingSystem.IsWindows())
        {
            Assert.AreEqual(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(recoveryPath));
            Assert.AreEqual(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(keyPath));
            Assert.AreEqual(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(Path.GetDirectoryName(recoveryPath)!));
        }

        await restartedStore.DiscardAsync(TestContext.Current.CancellationToken);
        Assert.IsFalse(File.Exists(recoveryPath));
    }

    /// <summary>
    /// Verifies records at least 24 hours old are deleted before they can be recovered.
    /// </summary>
    [TestMethod]
    public async Task LoadAsync_WithExpiredState_DeletesRecovery()
    {
        var repository = CreateRepository("expired-repository");
        var store = await RevertUndoStore.CreateAsync(
            repository,
            _environment!,
            TimeProvider.System,
            TestContext.Current!.CancellationToken);
        var expired = new RevertUndoState(
            "expired patch"u8,
            CreatePrecondition(),
            DateTimeOffset.UtcNow - TimeSpan.FromHours(25));
        await store.SaveAsync(expired, TestContext.Current.CancellationToken);
        var recoveryPath = GetPath(store.RecoveryPath);

        var restartedStore = await RevertUndoStore.CreateAsync(
            repository,
            _environment!,
            TimeProvider.System,
            TestContext.Current.CancellationToken);
        var recovered = await restartedStore.LoadAsync(TestContext.Current.CancellationToken);

        Assert.IsNull(recovered);
        Assert.IsFalse(File.Exists(recoveryPath));
    }

    /// <summary>
    /// Verifies checksum corruption is rejected and the unusable recovery record is removed.
    /// </summary>
    [TestMethod]
    public async Task LoadAsync_WithCorruptRecord_RejectsAndDeletesRecovery()
    {
        var repository = CreateRepository("corrupt-repository");
        var store = await RevertUndoStore.CreateAsync(
            repository,
            _environment!,
            TimeProvider.System,
            TestContext.Current!.CancellationToken);
        await store.SaveAsync(
            store.CreateState("recoverable patch"u8, CreatePrecondition()),
            TestContext.Current.CancellationToken);
        var recoveryPath = GetPath(store.RecoveryPath);
        var bytes = File.ReadAllBytes(recoveryPath);
        bytes[^1] ^= 0xff;
        File.WriteAllBytes(recoveryPath, bytes);

        var recovered = await store.LoadAsync(TestContext.Current.CancellationToken);

        Assert.IsNull(recovered);
        Assert.IsNotNull(store.Warning);
        Assert.IsFalse(File.Exists(recoveryPath));
    }

    /// <summary>
    /// Verifies distinct repositories receive distinct opaque filenames under the same private key.
    /// </summary>
    [TestMethod]
    public async Task CreateAsync_WithDistinctRepositories_UsesDistinctRecoveryPaths()
    {
        var first = await RevertUndoStore.CreateAsync(
            CreateRepository("first-repository"),
            _environment!,
            TimeProvider.System,
            TestContext.Current!.CancellationToken);
        var second = await RevertUndoStore.CreateAsync(
            CreateRepository("second-repository"),
            _environment!,
            TimeProvider.System,
            TestContext.Current.CancellationToken);

        Assert.AreNotEqual(GetPath(first.RecoveryPath), GetPath(second.RecoveryPath));
    }

    private RepositoryLocation CreateRepository(string name)
    {
        var workTree = CreatePath(Path.Combine(_temporaryDirectory!, name));
        var gitDirectory = CreatePath(Path.Combine(_temporaryDirectory!, name, ".git"));
        return new RepositoryLocation(
            gitDirectory,
            gitDirectory,
            workTree,
            Prefix: null,
            RepositoryObjectFormat.Sha1,
            IsBare: false);
    }

    private static RepositoryPrecondition CreatePrecondition()
    {
        Assert.IsTrue(ObjectId.TryParseHex(
            "0123456789abcdef0123456789abcdef01234567"u8,
            out var objectId));
        return new RepositoryPrecondition(
            objectId,
            RefName.FromBytes("refs/heads/main"u8),
            Enumerable.Range(0, 32).Select(static value => (byte)value).ToArray());
    }

    private static GitPath CreatePath(string path)
        => OperatingSystem.IsWindows()
            ? GitPath.FromWindowsPath(path)
            : GitPath.FromUnixBytes(Encoding.UTF8.GetBytes(path));

    private static string GetPath(GitPath path)
        => path.Kind == NativePathKind.WindowsUtf16
            ? path.GetWindowsPath()
            : Encoding.UTF8.GetString(path.GetUnixBytes());
}
