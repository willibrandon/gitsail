using GitSail.Domain;
using GitSail.Git.Execution;
using System.Text;

namespace GitSail.UnitTests;

/// <summary>
/// Verifies remote initialization URL parsing keeps local paths and SSH data out of command syntax.
/// </summary>
[TestClass]
public sealed class RemoteInitializationTargetParserTests
{
    /// <summary>
    /// Verifies an absolute platform path becomes a typed local target.
    /// </summary>
    [TestMethod]
    public void Parse_WithAbsoluteLocalPath_ReturnsLocalTarget()
    {
        var path = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "repository.git"));

        var target = RemoteInitializationTargetParser.Parse(RemoteUrl.FromText(path));

        Assert.AreEqual(RemoteInitializationKind.Local, target.Kind);
        Assert.AreEqual(path, target.LocalPath);
        Assert.IsNull(target.SshDestination);
    }

    /// <summary>
    /// Verifies a local file URL becomes one absolute typed platform path.
    /// </summary>
    [TestMethod]
    public void Parse_WithFileUrl_ReturnsLocalTarget()
    {
        var path = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "space repository.git"));
        var url = new Uri(path).AbsoluteUri;

        var target = RemoteInitializationTargetParser.Parse(RemoteUrl.FromText(url));

        Assert.AreEqual(RemoteInitializationKind.Local, target.Kind);
        Assert.AreEqual(path, target.LocalPath);
    }

    /// <summary>
    /// Verifies an SSH URI separates decoded user, host, port, and path data.
    /// </summary>
    [TestMethod]
    public void Parse_WithSshUri_ReturnsStructuredSshTarget()
    {
        var target = RemoteInitializationTargetParser.Parse(
            RemoteUrl.FromText("ssh://developer@example.invalid:2222/srv/git/space%20repository.git"));

        Assert.AreEqual(RemoteInitializationKind.Ssh, target.Kind);
        Assert.AreEqual("developer", target.SshDestination?.User);
        Assert.AreEqual("example.invalid", target.SshDestination?.Host);
        Assert.AreEqual(2222, target.SshPort);
        Assert.AreEqual("/srv/git/space repository.git", Encoding.UTF8.GetString(target.RemotePath!));
    }

    /// <summary>
    /// Verifies an SSH URI retains percent-encoded non-Unicode path bytes exactly.
    /// </summary>
    [TestMethod]
    public void Parse_WithPercentEncodedRawSshPath_RetainsExactBytes()
    {
        var target = RemoteInitializationTargetParser.Parse(
            RemoteUrl.FromText("ssh://example.invalid/repositories/raw%FFname.git"));

        byte[] expected = [.. "/repositories/raw"u8, 0xFF, .. "name.git"u8];
        Assert.IsTrue(target.RemotePath!.AsSpan().SequenceEqual(expected));
    }

    /// <summary>
    /// Verifies an SCP-style URL separates destination and hostile-looking path bytes without interpolation.
    /// </summary>
    [TestMethod]
    public void Parse_WithScpStyleTarget_ReturnsStructuredSshTarget()
    {
        var target = RemoteInitializationTargetParser.Parse(
            RemoteUrl.FromText("developer@example.invalid:repositories/release;touch marker.git"));

        Assert.AreEqual(RemoteInitializationKind.Ssh, target.Kind);
        Assert.AreEqual("developer@example.invalid", target.SshDestination?.ToString());
        Assert.IsNull(target.SshPort);
        Assert.AreEqual(
            "repositories/release;touch marker.git",
            Encoding.UTF8.GetString(target.RemotePath!));
    }

    /// <summary>
    /// Verifies unsupported protocols, embedded passwords, and relative local paths are rejected.
    /// </summary>
    /// <param name="value">The unsafe or unsupported URL text.</param>
    [TestMethod]
    [DataRow("https://example.invalid/repository.git")]
    [DataRow("ssh://developer:password@example.invalid/repository.git")]
    [DataRow("relative/repository.git")]
    public void Parse_WithUnsupportedTarget_ThrowsRemoteInitializationException(string value)
    {
        _ = Assert.ThrowsExactly<RemoteInitializationException>(() =>
            RemoteInitializationTargetParser.Parse(RemoteUrl.FromText(value)));
    }
}
