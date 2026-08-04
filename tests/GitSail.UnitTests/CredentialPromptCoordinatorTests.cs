using GitSail.Git.Execution;
using GitSail.Ui;
using System.Security.Cryptography;
using System.Text;

namespace GitSail.UnitTests;

/// <summary>
/// Verifies nonpersistent prompt state binds responses and cancellation to exact request identities.
/// </summary>
[TestClass]
public sealed class CredentialPromptCoordinatorTests
{
    /// <summary>
    /// Verifies an exact current request accepts one response and clears presentation state.
    /// </summary>
    [TestMethod]
    public async Task Submit_WithCurrentRequest_ReturnsOwnedResponseAndClearsState()
    {
        using var coordinator = new CredentialPromptCoordinator();
        var responseTask = coordinator.RequestAsync(
            "Fetch origin",
            "Password:",
            CredentialPromptKind.Secret,
            TestContext.Current!.CancellationToken);
        var request = await WaitForCurrentAsync(coordinator);

        Assert.IsTrue(coordinator.Submit(request.Id, "secret"));
        var response = await responseTask;

        Assert.IsNotNull(response);
        try
        {
            Assert.AreEqual("secret", Encoding.UTF8.GetString(response));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(response);
        }

        Assert.IsNull(coordinator.Current);
        Assert.IsFalse(coordinator.Submit(request.Id, "stale"));
    }

    /// <summary>
    /// Verifies cancelling an exact current request returns no response and clears state.
    /// </summary>
    [TestMethod]
    public async Task Cancel_WithCurrentRequest_ReturnsNullAndClearsState()
    {
        using var coordinator = new CredentialPromptCoordinator();
        var responseTask = coordinator.RequestAsync(
            "SSH initialization",
            "Continue connecting?",
            CredentialPromptKind.Confirmation,
            TestContext.Current!.CancellationToken);
        var request = await WaitForCurrentAsync(coordinator);

        Assert.IsTrue(coordinator.Cancel(request.Id));

        Assert.IsNull(await responseTask);
        Assert.IsNull(coordinator.Current);
    }

    private static async Task<CredentialPromptRequest> WaitForCurrentAsync(
        CredentialPromptCoordinator coordinator)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (coordinator.Current is { } request)
            {
                return request;
            }

            await Task.Delay(10, TestContext.Current!.CancellationToken);
        }

        Assert.Fail("The credential request was not published.");
        throw new InvalidOperationException();
    }
}
