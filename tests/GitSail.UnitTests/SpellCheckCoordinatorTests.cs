using GitSail.Domain;
using GitSail.Ui;

namespace GitSail.UnitTests;

/// <summary>
/// Verifies live spelling cancellation, exact-version publication, failure disablement, and retry.
/// </summary>
[TestClass]
public sealed class SpellCheckCoordinatorTests
{
    /// <summary>
    /// Verifies a newer editor version cancels its predecessor and alone publishes issues.
    /// </summary>
    [TestMethod]
    public async Task Schedule_WithNewerVersion_CancelsAndRejectsSupersededWork()
    {
        await using var operations = new OperationSupervisor(TimeProvider.System);
        var spelling = new SpellingState();
        var currentVersion = 1L;
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstCanceled = false;
        using var coordinator = new SpellCheckCoordinator(
            operations,
            spelling,
            () => currentVersion,
            async (_, version, _, cancellationToken) =>
            {
                if (version == 1)
                {
                    firstStarted.TrySetResult();
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    }
                    finally
                    {
                        firstCanceled = cancellationToken.IsCancellationRequested;
                    }
                }

                return CreateResult(version, "wierd");
            },
            static () => { },
            TimeProvider.System,
            TimeSpan.Zero);

        coordinator.Schedule("first", documentVersion: 1, dictionary: string.Empty);
        await firstStarted.Task.WaitAsync(TestContext.Current!.CancellationToken);
        currentVersion = 2;
        coordinator.Schedule("second", documentVersion: 2, dictionary: string.Empty);
        await operations.JoinAsync(TestContext.Current.CancellationToken);

        Assert.IsTrue(firstCanceled);
        Assert.HasCount(1, spelling.Issues);
        Assert.AreEqual("wierd", spelling.Issues[0].Word);
        Assert.IsFalse(spelling.IsChecking);
    }

    /// <summary>
    /// Verifies failure disables automatic retries while an explicit check can recover.
    /// </summary>
    [TestMethod]
    public async Task CheckNow_AfterFailure_RetriesDisabledCheckerWithoutBlockingEditorState()
    {
        await using var operations = new OperationSupervisor(TimeProvider.System);
        var spelling = new SpellingState();
        var callCount = 0;
        using var coordinator = new SpellCheckCoordinator(
            operations,
            spelling,
            static () => 4,
            (_, version, _, _) =>
            {
                callCount++;
                return callCount == 1
                    ? Task.FromException<SpellCheckResult>(new SpellCheckException("dictionary is missing"))
                    : Task.FromResult(CreateResult(version, "teh"));
            },
            static () => { },
            TimeProvider.System,
            TimeSpan.Zero);

        coordinator.Schedule("teh", documentVersion: 4, dictionary: "missing");
        await operations.JoinAsync(TestContext.Current!.CancellationToken);
        coordinator.Schedule("teh", documentVersion: 4, dictionary: "missing");
        await operations.JoinAsync(TestContext.Current.CancellationToken);

        Assert.AreEqual(1, callCount);
        Assert.IsFalse(spelling.IsAvailable);
        StringAssert.Contains(spelling.StatusText, "dictionary is missing", StringComparison.Ordinal);

        coordinator.CheckNow("teh", documentVersion: 4, dictionary: string.Empty);
        await operations.JoinAsync(TestContext.Current.CancellationToken);

        Assert.AreEqual(2, callCount);
        Assert.IsTrue(spelling.IsAvailable);
        Assert.HasCount(1, spelling.Issues);
    }

    /// <summary>
    /// Verifies a canceled checker failure cannot disable a newer retry of the same editor version.
    /// </summary>
    [TestMethod]
    public async Task CheckNow_WithSameVersion_IgnoresLateFailureFromSupersededRequest()
    {
        await using var operations = new OperationSupervisor(TimeProvider.System);
        var spelling = new SpellingState();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callCount = 0;
        using var coordinator = new SpellCheckCoordinator(
            operations,
            spelling,
            static () => 9,
            async (_, version, _, _) =>
            {
                var call = Interlocked.Increment(ref callCount);
                if (call == 1)
                {
                    firstStarted.TrySetResult();
                    await releaseFirst.Task;
                    throw new SpellCheckException("late stale failure");
                }

                secondCompleted.TrySetResult();
                return CreateResult(version, "teh");
            },
            static () => { },
            TimeProvider.System,
            TimeSpan.Zero);

        coordinator.Schedule("teh", documentVersion: 9, dictionary: string.Empty);
        await firstStarted.Task.WaitAsync(TestContext.Current!.CancellationToken);
        coordinator.CheckNow("teh", documentVersion: 9, dictionary: string.Empty);
        await secondCompleted.Task.WaitAsync(TestContext.Current.CancellationToken);
        releaseFirst.TrySetResult();
        await operations.JoinAsync(TestContext.Current.CancellationToken);

        Assert.AreEqual(2, callCount);
        Assert.IsTrue(spelling.IsAvailable);
        Assert.HasCount(1, spelling.Issues);
        Assert.DoesNotContain("late stale failure", spelling.StatusText, StringComparison.Ordinal);
    }

    private static SpellCheckResult CreateResult(long documentVersion, string word)
        => new(
            documentVersion,
            string.Empty,
            "Aspell 0.60.8",
            [new SpellingIssue(0, word.Length, word, ["replacement"])]);
}
