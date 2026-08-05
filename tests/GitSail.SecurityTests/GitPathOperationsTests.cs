using GitSail.Domain;
using GitSail.Testing;
using System.Text;

namespace GitSail.SecurityTests;

/// <summary>
/// Verifies exact repository-relative path composition for lazy tree navigation.
/// </summary>
[TestClass]
public sealed class GitPathOperationsTests
{
    /// <summary>
    /// Verifies root composition returns the exact supplied immediate name.
    /// </summary>
    [TestMethod]
    public void Combine_AtRoot_ReturnsExactName()
    {
        var name = CreatePath("child");

        var result = GitPathOperations.Combine(directory: null, name);

        Assert.AreSame(name, result);
    }

    /// <summary>
    /// Verifies nested composition inserts one Git directory separator without display conversion.
    /// </summary>
    [TestMethod]
    public void Combine_WithDirectory_ReturnsExactNestedPath()
    {
        var result = GitPathOperations.Combine(CreatePath("parent/"), CreatePath("child"));

        Assert.AreEqual("parent/child", result.DisplayText);
    }

    /// <summary>
    /// Verifies non-UTF-8 Unix bytes survive nested path composition exactly.
    /// </summary>
    [TestMethod]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
    public void Combine_WithNonUtf8UnixName_RetainsExactBytes()
    {
        var result = GitPathOperations.Combine(
            GitPath.FromUnixBytes("parent"u8),
            GitPath.FromUnixBytes([(byte)'c', 0xff]));

        TestSeq.AreEqual(
            new byte[]
            {
                (byte)'p',
                (byte)'a',
                (byte)'r',
                (byte)'e',
                (byte)'n',
                (byte)'t',
                (byte)'/',
                (byte)'c',
                0xff,
            },
            result.GetUnixBytes().ToArray());
    }

    /// <summary>
    /// Verifies redundant current-directory components and separators normalize to an exact Git path.
    /// </summary>
    [TestMethod]
    public void NormalizeDirectory_WithRedundantComponents_ReturnsCanonicalGitPath()
    {
        var input = OperatingSystem.IsWindows()
            ? GitPath.FromWindowsPath(@".\parent\\child\")
            : GitPath.FromUnixBytes("./parent//child/"u8);

        var result = GitPathOperations.NormalizeDirectory(input);

        Assert.IsNotNull(result);
        Assert.AreEqual("parent/child", result.DisplayText);
    }

    /// <summary>
    /// Verifies the current-directory operand selects the repository root.
    /// </summary>
    [TestMethod]
    public void NormalizeDirectory_WithCurrentDirectory_ReturnsRoot()
    {
        var result = GitPathOperations.NormalizeDirectory(CreatePath("."));

        Assert.IsNull(result);
    }

    /// <summary>
    /// Verifies absolute directories cannot escape repository-relative tree selection.
    /// </summary>
    [TestMethod]
    public void NormalizeDirectory_WithAbsolutePath_ThrowsArgumentException()
    {
        var input = OperatingSystem.IsWindows()
            ? GitPath.FromWindowsPath(@"C:\outside")
            : GitPath.FromUnixBytes("/outside"u8);

        Assert.Throws<ArgumentException>(() => GitPathOperations.NormalizeDirectory(input));
    }

    /// <summary>
    /// Verifies parent traversal is rejected instead of being resolved outside the selected tree path.
    /// </summary>
    [TestMethod]
    public void NormalizeDirectory_WithParentTraversal_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() =>
            GitPathOperations.NormalizeDirectory(CreatePath("parent/../outside")));
    }

    private static GitPath CreatePath(string value)
        => OperatingSystem.IsWindows()
            ? GitPath.FromWindowsPath(value)
            : GitPath.FromUnixBytes(Encoding.UTF8.GetBytes(value));
}
