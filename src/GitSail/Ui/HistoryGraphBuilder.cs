using GitSail.Domain;
using System.Collections.Immutable;
using System.Text;

namespace GitSail.Ui;

/// <summary>
/// Builds a deterministic bounded lane presentation from structured commit parents.
/// </summary>
internal static class HistoryGraphBuilder
{
    private const int MaximumVisibleLanes = 6;

    /// <summary>
    /// Creates one graph-prefixed item for every commit in history order.
    /// </summary>
    /// <param name="commits">The structured commits in display order.</param>
    /// <returns>The ordered history rows with stable bounded lane prefixes.</returns>
    internal static ImmutableArray<HistoryWorkspaceItem> Build(ImmutableArray<HistoryCommit> commits)
    {
        var result = ImmutableArray.CreateBuilder<HistoryWorkspaceItem>(commits.Length);
        var lanes = new List<ObjectId>();
        foreach (var commit in commits)
        {
            var lane = lanes.FindIndex(candidate => candidate.Equals(commit.ObjectId));
            if (lane < 0)
            {
                lane = 0;
                lanes.Insert(0, commit.ObjectId);
            }

            result.Add(new HistoryWorkspaceItem(commit, Render(lanes, lane)));
            lanes.RemoveAt(lane);
            for (var parentIndex = commit.Parents.Length - 1; parentIndex >= 0; parentIndex--)
            {
                var parent = commit.Parents[parentIndex];
                if (!lanes.Any(candidate => candidate.Equals(parent)))
                {
                    lanes.Insert(Math.Min(lane, lanes.Count), parent);
                }
            }

            if (lanes.Count > MaximumVisibleLanes)
            {
                lanes.RemoveRange(MaximumVisibleLanes, lanes.Count - MaximumVisibleLanes);
            }
        }

        return result.MoveToImmutable();
    }

    private static string Render(List<ObjectId> lanes, int commitLane)
    {
        var builder = new StringBuilder((MaximumVisibleLanes * 2) + 1);
        var visibleCount = Math.Min(lanes.Count, MaximumVisibleLanes);
        for (var index = 0; index < visibleCount; index++)
        {
            if (index > 0)
            {
                builder.Append(' ');
            }

            builder.Append(index == commitLane ? '●' : '│');
        }

        if (lanes.Count > MaximumVisibleLanes)
        {
            builder.Append(" …");
        }

        return builder.ToString();
    }
}
