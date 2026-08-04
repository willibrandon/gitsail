using GitSail.Domain;
using GitSail.Git.Execution;
using System.Text;

namespace GitSail.ServiceTests;

/// <summary>
/// Verifies one-time authentication and exact-path binding for sequence-editor requests.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class RebaseSequenceEditorRequestTests
{
    private RebaseSequenceEditorRequestHandle? _handle;

    /// <summary>
    /// Removes any request left intentionally unused by a failed-authentication test.
    /// </summary>
    [TestCleanup]
    public async Task CleanupAsync()
    {
        if (_handle is not null)
        {
            await RebaseSequenceEditorRequest.DeleteIfExistsAsync(
                _handle,
                CancellationToken.None);
        }
    }

    /// <summary>
    /// Verifies a valid secret consumes a request exactly once and returns the bound path.
    /// </summary>
    [TestMethod]
    public async Task ConsumeAsync_WithValidRequest_ReturnsExactPathAndDeletesRequest()
    {
        var expected = CreateNativePath(Path.Combine(Path.GetTempPath(), "repo", ".git", "rebase-merge", "git-rebase-todo"));
        _handle = await RebaseSequenceEditorRequest.CreateAsync(
            expected,
            TimeProvider.System,
            TestContext.Current!.CancellationToken);

        var actual = await RebaseSequenceEditorRequest.ConsumeAsync(
            _handle.FilePathText,
            _handle.Secret,
            expected,
            TimeProvider.System,
            TestContext.Current.CancellationToken);

        Assert.AreEqual(expected, actual);
        Assert.IsFalse(File.Exists(_handle.FilePathText));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            RebaseSequenceEditorRequest.ConsumeAsync(
                _handle.FilePathText,
                _handle.Secret,
                expected,
                TimeProvider.System,
                TestContext.Current.CancellationToken));
        _handle = null;
    }

    /// <summary>
    /// Verifies a wrong secret cannot authenticate or consume the protected request.
    /// </summary>
    [TestMethod]
    public async Task ConsumeAsync_WithWrongSecret_RejectsWithoutDeletingRequest()
    {
        var expected = CreateNativePath(Path.Combine(Path.GetTempPath(), "repo", ".git", "rebase-merge", "git-rebase-todo"));
        _handle = await RebaseSequenceEditorRequest.CreateAsync(
            expected,
            TimeProvider.System,
            TestContext.Current!.CancellationToken);
        var wrongSecret = new string('0', 64);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            RebaseSequenceEditorRequest.ConsumeAsync(
                _handle.FilePathText,
                wrongSecret,
                expected,
                TimeProvider.System,
                TestContext.Current.CancellationToken));

        Assert.IsTrue(File.Exists(_handle.FilePathText));
    }

    /// <summary>
    /// Verifies an authenticated callback cannot redirect editing to a different file.
    /// </summary>
    [TestMethod]
    public async Task ConsumeAsync_WithDifferentTodoPath_RejectsAndConsumesRequest()
    {
        var expected = CreateNativePath(Path.Combine(Path.GetTempPath(), "repo", ".git", "rebase-merge", "git-rebase-todo"));
        var different = CreateNativePath(Path.Combine(Path.GetTempPath(), "other", "git-rebase-todo"));
        _handle = await RebaseSequenceEditorRequest.CreateAsync(
            expected,
            TimeProvider.System,
            TestContext.Current!.CancellationToken);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            RebaseSequenceEditorRequest.ConsumeAsync(
                _handle.FilePathText,
                _handle.Secret,
                different,
                TimeProvider.System,
                TestContext.Current.CancellationToken));

        Assert.IsFalse(File.Exists(_handle.FilePathText));
        _handle = null;
    }

    private static GitPath CreateNativePath(string path)
        => OperatingSystem.IsWindows()
            ? GitPath.FromWindowsPath(path)
            : GitPath.FromUnixBytes(Encoding.UTF8.GetBytes(path));
}
