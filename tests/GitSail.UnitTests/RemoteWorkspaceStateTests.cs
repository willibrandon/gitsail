using GitSail.Domain;
using GitSail.Ui;

namespace GitSail.UnitTests;

/// <summary>
/// Verifies controlled searchable remote-window focus and credential-safe display state.
/// </summary>
[TestClass]
public sealed class RemoteWorkspaceStateTests
{
    /// <summary>
    /// Verifies filtering across names and redacted URLs retains exact-name focus.
    /// </summary>
    [TestMethod]
    public void ApplyCatalogAndSetFilter_WithMultipleRemotes_RetainsExactControlledFocus()
    {
        var origin = CreateRemote("origin", "https://example.invalid/team/repository");
        var upstream = CreateRemote("upstream", "ssh://git.example.invalid/project");
        var state = new RemoteWorkspaceState();

        state.ApplyCatalog(new RemoteCatalog([origin, upstream]));
        state.Focus(1);
        state.SetFilter("GIT.EXAMPLE");

        Assert.HasCount(1, state.VisibleItems);
        Assert.AreSame(upstream, state.FocusedItem?.Remote);
        state.SetFilter(string.Empty);
        Assert.AreSame(upstream, state.FocusedItem?.Remote);
    }

    /// <summary>
    /// Verifies list text never exposes configured URL user information or query values.
    /// </summary>
    [TestMethod]
    public void ToString_WithCredentialUrl_ReturnsRedactedRow()
    {
        var remote = CreateRemote(
            "origin",
            "https://person:password@example.invalid/repository?token=secret");

        var text = new RemoteWorkspaceItem(remote).ToString();

        Assert.AreEqual("origin | https://example.invalid/repository?<redacted>", text);
        Assert.IsFalse(text.Contains("password", StringComparison.Ordinal));
        Assert.IsFalse(text.Contains("secret", StringComparison.Ordinal));
    }

    private static RemoteInfo CreateRemote(string name, string url)
    {
        var remoteUrl = RemoteUrl.FromText(url);
        return new RemoteInfo(RemoteName.FromBytes(System.Text.Encoding.UTF8.GetBytes(name)), [remoteUrl], [remoteUrl]);
    }
}
