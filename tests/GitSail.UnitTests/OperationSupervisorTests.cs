using GitSail.Domain;

namespace GitSail.UnitTests;

/// <summary>
/// Verifies owned operation lifecycle, cancellation, progress, failure observation, and joining.
/// </summary>
[TestClass]
public sealed class OperationSupervisorTests
{
    /// <summary>
    /// Verifies a background operation publishes ordered progress and completion before leaving the active set.
    /// </summary>
    [TestMethod]
    public async Task Start_WithProgress_PublishesOrderedLifecycleAndJoins()
    {
        await using var supervisor = new OperationSupervisor(TimeProvider.System);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var id = supervisor.Start("history-preview", async context =>
        {
            context.Report("Loading commit", 0.25);
            entered.TrySetResult();
            await release.Task.WaitAsync(context.CancellationToken);
        });

        await entered.Task.WaitAsync(TestContext.Current!.CancellationToken);
        var activeOperations = supervisor.ActiveOperations;
        Assert.HasCount(1, activeOperations);
        var active = activeOperations[0];
        Assert.AreEqual(id, active.Id);
        Assert.AreEqual(OperationState.Running, active.State);
        Assert.AreEqual("Loading commit", active.Detail);
        Assert.AreEqual(0.25, active.Progress);

        release.TrySetResult();
        await supervisor.JoinAsync(TestContext.Current!.CancellationToken);

        var snapshots = Drain(supervisor);
        CollectionAssert.AreEqual(
            new[] { OperationState.Started, OperationState.Running, OperationState.Completed },
            snapshots.Select(static snapshot => snapshot.State).ToArray());
        Assert.IsEmpty(supervisor.ActiveOperations);
        Assert.AreEqual(1d, snapshots[^1].Progress);
        Assert.IsNull(snapshots[^1].Failure);
    }

    /// <summary>
    /// Verifies targeted cancellation stops only the selected operation and records its cancellation exception.
    /// </summary>
    [TestMethod]
    public async Task Cancel_WithActiveOperation_PublishesCanceledAndJoins()
    {
        await using var supervisor = new OperationSupervisor(TimeProvider.System);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var id = supervisor.Start("watch", async context =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken);
        });

        await entered.Task.WaitAsync(TestContext.Current!.CancellationToken);
        Assert.IsTrue(supervisor.Cancel(id));
        await supervisor.JoinAsync(TestContext.Current!.CancellationToken);

        var final = Drain(supervisor)[^1];
        Assert.AreEqual(OperationState.Canceled, final.State);
        Assert.IsInstanceOfType<OperationCanceledException>(final.Failure);
        Assert.IsFalse(supervisor.Cancel(id));
    }

    /// <summary>
    /// Verifies an unawaited background failure is observed and published without faulting supervisor joining.
    /// </summary>
    [TestMethod]
    public async Task Start_WithFailure_ObservesFailureAndKeepsJoinSuccessful()
    {
        await using var supervisor = new OperationSupervisor(TimeProvider.System);
        supervisor.Start(
            "autosave",
            static _ => throw new InvalidOperationException("write failed"));

        await supervisor.JoinAsync(TestContext.Current!.CancellationToken);

        var final = Drain(supervisor)[^1];
        Assert.AreEqual(OperationState.Failed, final.State);
        var failure = Assert.IsInstanceOfType<InvalidOperationException>(final.Failure);
        Assert.AreEqual("write failed", failure.Message);
    }

    /// <summary>
    /// Verifies an awaited operation retains normal task failure propagation after supervisor observation.
    /// </summary>
    [TestMethod]
    public async Task RunAsync_WithFailure_ObservesAndPropagatesExactFailure()
    {
        await using var supervisor = new OperationSupervisor(TimeProvider.System);

        var failure = await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            supervisor.RunAsync(
                "foreground",
                static _ => throw new InvalidDataException("invalid operation input"),
                TestContext.Current!.CancellationToken));

        Assert.AreEqual("invalid operation input", failure.Message);
        var final = Drain(supervisor)[^1];
        Assert.AreEqual(OperationState.Failed, final.State);
        Assert.AreSame(failure, final.Failure);
    }

    /// <summary>
    /// Verifies shutdown cancels and joins active work before rejecting every later operation.
    /// </summary>
    [TestMethod]
    public async Task ShutdownAsync_WithActiveOperation_CancelsJoinsAndRejectsNewWork()
    {
        await using var supervisor = new OperationSupervisor(TimeProvider.System);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        supervisor.Start("long-running", async context =>
        {
            entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken);
            }
            finally
            {
                exited.TrySetResult();
            }
        });

        await entered.Task.WaitAsync(TestContext.Current!.CancellationToken);
        await supervisor.ShutdownAsync(TestContext.Current!.CancellationToken);
        await exited.Task.WaitAsync(TestContext.Current!.CancellationToken);

        Assert.IsEmpty(supervisor.ActiveOperations);
        _ = Assert.ThrowsExactly<ObjectDisposedException>(() =>
            supervisor.Start("late", static _ => Task.CompletedTask));
    }

    private static List<OperationSnapshot> Drain(OperationSupervisor supervisor)
    {
        var snapshots = new List<OperationSnapshot>();
        while (supervisor.Updates.TryRead(out var snapshot))
        {
            snapshots.Add(snapshot);
        }

        return snapshots;
    }
}
