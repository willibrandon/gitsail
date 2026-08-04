using GitSail.Domain;

namespace GitSail.UnitTests;

/// <summary>
/// Verifies exception-safe serialization of typed repository mutation leases.
/// </summary>
[TestClass]
public sealed class RepositoryMutationCoordinatorTests
{
    /// <summary>
    /// Verifies that a second mutation waits until the active lease is released.
    /// </summary>
    [TestMethod]
    public async Task AcquireAsync_WithActiveLease_SerializesMutations()
    {
        using var coordinator = new RepositoryMutationCoordinator();
        var first = await coordinator.AcquireAsync(
            RepositoryMutationPurpose.UpdateIndex,
            TestContext.Current!.CancellationToken);

        var pending = coordinator.AcquireAsync(
            RepositoryMutationPurpose.Commit,
            TestContext.Current.CancellationToken).AsTask();

        Assert.IsFalse(pending.IsCompleted);
        await first.DisposeAsync();
        await using var second = await pending;
        Assert.AreEqual(RepositoryMutationPurpose.Commit, second.Purpose);
    }
}
