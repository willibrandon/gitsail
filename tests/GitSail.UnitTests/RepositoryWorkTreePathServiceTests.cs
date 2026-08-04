using GitSail.Domain;
using GitSail.Git.Execution;
using GitSail.Testing;

namespace GitSail.UnitTests;

/// <summary>
/// Verifies exact native worktree path joining and traversal rejection on every platform.
/// </summary>
[TestClass]
public sealed class RepositoryWorkTreePathServiceTests
{
    /// <summary>
    /// Verifies a legal repository-relative path resolves beneath the exact canonical worktree root.
    /// </summary>
    [TestMethod]
    public void Resolve_WithLegalRelativePath_ReturnsExactAbsolutePath()
    {
        if (OperatingSystem.IsWindows())
        {
            var repository = CreateRepository(GitPath.FromWindowsPath("C:\\repository"));

            var actual = RepositoryWorkTreePathService.Resolve(
                repository,
                GitPath.FromWindowsPath("dir/file.txt"));

            Assert.AreEqual("C:\\repository\\dir\\file.txt", actual.GetWindowsPath());
            return;
        }

        var root = GitPath.FromUnixBytes("/repository"u8);
        var unixRepository = CreateRepository(root);
        byte[] relativeBytes = [(byte)'d', (byte)'i', (byte)'r', (byte)'/', 0xff];

        var resolved = RepositoryWorkTreePathService.Resolve(
            unixRepository,
            GitPath.FromUnixBytes(relativeBytes));

        byte[] expected = [.. "/repository/dir/"u8, 0xff];
        TestSeq.AreEqual(expected, resolved.GetUnixBytes().ToArray());
    }

    /// <summary>
    /// Verifies parent traversal and absolute paths are rejected before native path construction.
    /// </summary>
    [TestMethod]
    public void Resolve_WithTraversalPath_ThrowsInvalidDataException()
    {
        var root = OperatingSystem.IsWindows()
            ? GitPath.FromWindowsPath("C:\\repository")
            : GitPath.FromUnixBytes("/repository"u8);
        var traversal = OperatingSystem.IsWindows()
            ? GitPath.FromWindowsPath("..\\outside.txt")
            : GitPath.FromUnixBytes("../outside.txt"u8);

        Assert.ThrowsExactly<InvalidDataException>(() =>
            RepositoryWorkTreePathService.Resolve(CreateRepository(root), traversal));
    }

    /// <summary>
    /// Verifies Windows alternate data stream syntax is rejected as an unsafe path component.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public void Resolve_WithAlternateDataStream_ThrowsInvalidDataException()
    {
        var repository = CreateRepository(GitPath.FromWindowsPath("C:\\repository"));

        Assert.ThrowsExactly<InvalidDataException>(() => RepositoryWorkTreePathService.Resolve(
            repository,
            GitPath.FromWindowsPath("file.txt:stream")));
    }

    private static RepositoryLocation CreateRepository(GitPath root)
        => new(
            root,
            root,
            root,
            Prefix: null,
            RepositoryObjectFormat.Sha1,
            IsBare: false);
}
