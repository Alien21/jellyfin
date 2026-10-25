using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MediaBrowser.Model.Entities;
using MediaBrowser.Providers.Manager;
using Xunit;

namespace Jellyfin.Providers.Tests.Manager;

public class LibraryRefreshProgressTrackerTests
{
    [Fact]
    public void Begin_SnapshotsDistinctPathsAndKeepsQueuedLibrariesVisible()
    {
        var tracker = new LibraryRefreshProgressTracker();
        var library = Guid.NewGuid();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var third = Guid.NewGuid();
        var paths = new List<Guid> { first, second, first, third, second };
        var plan = new Dictionary<Guid, IReadOnlyList<Guid>> { [library] = paths };

        var operation = tracker.Begin(plan, aggregate: false);
        paths.Clear();
        plan.Clear();

        AssertSnapshot(tracker.Get(library), operation, "Sequential", "Queued", 0, 0, 3);
        Assert.Equal(new[] { first, second, third }, tracker.GetPaths(operation, library));
    }

    [Fact]
    public void SequentialProgress_ReportsTheCurrentPathUntilTheWholePlanCompletes()
    {
        var tracker = new LibraryRefreshProgressTracker();
        var library = Guid.NewGuid();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var third = Guid.NewGuid();
        var operation = tracker.Begin(Plan(library, first, second, third), aggregate: false);

        tracker.StartPath(operation, first);
        tracker.ReportPath(operation, first, 45);
        AssertSnapshot(tracker.Get(library), operation, "Sequential", "Active", 45, 1, 3);

        tracker.CompletePath(operation, first);
        AssertSnapshot(tracker.Get(library), operation, "Sequential", "Active", 100, 1, 3);

        tracker.StartPath(operation, second);
        tracker.ReportPath(operation, second, 20);
        AssertSnapshot(tracker.Get(library), operation, "Sequential", "Active", 20, 2, 3);

        tracker.CompletePath(operation, second);
        tracker.StartPath(operation, third);
        tracker.CompletePath(operation, third);
        AssertSnapshot(tracker.Get(library), operation, "Sequential", "Idle", 100, 3, 3);

        tracker.End(operation);
        Assert.Null(tracker.Get(library));
    }

    [Fact]
    public void AggregateProgress_IncludesPendingAndCompletedPathsWithoutDroppingAtTransitions()
    {
        var tracker = new LibraryRefreshProgressTracker();
        var library = Guid.NewGuid();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var third = Guid.NewGuid();
        var operation = tracker.Begin(Plan(library, first, second, third), aggregate: true);
        AssertSnapshot(tracker.Get(library), operation, "Aggregate", "Queued", 0, 0, 3);

        tracker.StartPath(operation, first);
        tracker.StartPath(operation, second);
        tracker.ReportPath(operation, first, 20);
        tracker.ReportPath(operation, second, 80);
        AssertSnapshot(tracker.Get(library), operation, "Aggregate", "Active", 100.0 / 3, 2, 3);

        tracker.CompletePath(operation, first);
        AssertSnapshot(tracker.Get(library), operation, "Aggregate", "Active", 60, 2, 3);

        tracker.CompletePath(operation, second);
        AssertSnapshot(tracker.Get(library), operation, "Aggregate", "Active", 200.0 / 3, 2, 3);

        tracker.StartPath(operation, third);
        AssertSnapshot(tracker.Get(library), operation, "Aggregate", "Active", 200.0 / 3, 3, 3);
        tracker.ReportPath(operation, third, 50);
        AssertSnapshot(tracker.Get(library), operation, "Aggregate", "Active", 250.0 / 3, 3, 3);

        tracker.CompletePath(operation, third);
        AssertSnapshot(tracker.Get(library), operation, "Aggregate", "Idle", 100, 3, 3);
    }

    [Fact]
    public void SharedPaths_UpdateEachOwningLibraryWithoutChangingItsDenominator()
    {
        var tracker = new LibraryRefreshProgressTracker();
        var firstLibrary = Guid.NewGuid();
        var secondLibrary = Guid.NewGuid();
        var shared = Guid.NewGuid();
        var firstOnly = Guid.NewGuid();
        var secondOnly = Guid.NewGuid();
        var changed = new List<Guid>();
        tracker.Changed += changed.Add;
        var operation = tracker.Begin(
            new Dictionary<Guid, IReadOnlyList<Guid>>
            {
                [firstLibrary] = new[] { shared, firstOnly },
                [secondLibrary] = new[] { shared, secondOnly }
            },
            aggregate: true);
        changed.Clear();

        tracker.StartPath(operation, shared);
        Assert.Equal(new[] { firstLibrary, secondLibrary }, changed);
        tracker.ReportPath(operation, shared, 40);
        AssertSnapshot(tracker.Get(firstLibrary), operation, "Aggregate", "Active", 20, 1, 2);
        AssertSnapshot(tracker.Get(secondLibrary), operation, "Aggregate", "Active", 20, 1, 2);

        tracker.CompletePath(operation, shared);
        tracker.StartPath(operation, firstOnly);
        tracker.ReportPath(operation, firstOnly, 60);
        AssertSnapshot(tracker.Get(firstLibrary), operation, "Aggregate", "Active", 80, 2, 2);
        AssertSnapshot(tracker.Get(secondLibrary), operation, "Aggregate", "Active", 50, 1, 2);
    }

    [Fact]
    public void OverlappingOperations_SelectActiveAggregateThenEarliestActiveAndQueued()
    {
        var tracker = new LibraryRefreshProgressTracker();
        var library = Guid.NewGuid();
        var path = Guid.NewGuid();
        var first = tracker.Begin(Plan(library, path), aggregate: false);
        var second = tracker.Begin(Plan(library, path), aggregate: false);
        var aggregate = tracker.Begin(Plan(library, path), aggregate: true);
        Assert.Equal(first, tracker.Get(library)?.OperationId);

        tracker.StartPath(second, path);
        Assert.Equal(second, tracker.Get(library)?.OperationId);
        tracker.StartPath(first, path);
        Assert.Equal(first, tracker.Get(library)?.OperationId);
        tracker.StartPath(aggregate, path);
        Assert.Equal(aggregate, tracker.Get(library)?.OperationId);

        tracker.CompletePath(aggregate, path);
        Assert.Equal(first, tracker.Get(library)?.OperationId);
        tracker.End(aggregate);
        tracker.End(first);
        Assert.Equal(second, tracker.Get(library)?.OperationId);

        tracker.CompletePath(second, path);
        var queued = tracker.Begin(Plan(library, path), aggregate: false);
        Assert.Equal(queued, tracker.Get(library)?.OperationId);
        tracker.End(queued);
        Assert.Equal(second, tracker.Get(library)?.OperationId);
    }

    [Fact]
    public void EndingOneOperation_PreservesOtherPlansAndIgnoresDelayedReports()
    {
        var tracker = new LibraryRefreshProgressTracker();
        var library = Guid.NewGuid();
        var path = Guid.NewGuid();
        var first = tracker.Begin(Plan(library, path), aggregate: false);
        var second = tracker.Begin(Plan(library, path), aggregate: false);
        var changes = 0;
        tracker.Changed += _ => changes++;
        tracker.StartPath(first, path);
        tracker.ReportPath(first, path, 60);

        tracker.End(first);
        AssertSnapshot(tracker.Get(library), second, "Sequential", "Queued", 0, 0, 1);
        var beforeLateReports = changes;
        tracker.StartPath(first, path);
        tracker.ReportPath(first, path, 99);
        tracker.CompletePath(first, path);
        tracker.End(first);
        tracker.ReportPath(second, Guid.NewGuid(), 50);
        Assert.Equal(beforeLateReports, changes);
        Assert.Empty(tracker.GetPaths(first, library));
        Assert.Empty(tracker.GetPaths(second, Guid.NewGuid()));

        tracker.End(second);
        Assert.Null(tracker.Get(library));
    }

    [Fact]
    public void PathProgress_RejectsInvalidOrRegressingValuesAndRepeatedStarts()
    {
        var tracker = new LibraryRefreshProgressTracker();
        var library = Guid.NewGuid();
        var path = Guid.NewGuid();
        var operation = tracker.Begin(Plan(library, path), aggregate: false);
        tracker.ReportPath(operation, path, 50);
        tracker.CompletePath(operation, path);
        AssertSnapshot(tracker.Get(library), operation, "Sequential", "Queued", 0, 0, 1);

        tracker.StartPath(operation, path);
        tracker.ReportPath(operation, path, 30);
        foreach (var invalidOrEarlier in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -10, 0, 20, 30 })
        {
            tracker.ReportPath(operation, path, invalidOrEarlier);
        }

        tracker.StartPath(operation, path);
        AssertSnapshot(tracker.Get(library), operation, "Sequential", "Active", 30, 1, 1);
        tracker.ReportPath(operation, path, 150);
        tracker.ReportPath(operation, path, 10);
        AssertSnapshot(tracker.Get(library), operation, "Sequential", "Active", 100, 1, 1);
        tracker.CompletePath(operation, path);
        tracker.ReportPath(operation, path, 50);
        tracker.StartPath(operation, path);
        AssertSnapshot(tracker.Get(library), operation, "Sequential", "Idle", 100, 1, 1);
    }

    [Fact]
    public void EmptyPlans_RemainFiniteAndCanBeRemoved()
    {
        var tracker = new LibraryRefreshProgressTracker();
        var noLibraries = tracker.Begin(new Dictionary<Guid, IReadOnlyList<Guid>>(), aggregate: true);
        Assert.NotEqual(Guid.Empty, noLibraries);
        Assert.Null(tracker.Get(Guid.NewGuid()));
        tracker.End(noLibraries);

        var library = Guid.NewGuid();
        var emptyLibrary = tracker.Begin(Plan(library), aggregate: true);
        AssertSnapshot(tracker.Get(library), emptyLibrary, "Aggregate", "Idle", 0, 0, 0);
        Assert.Empty(tracker.GetPaths(emptyLibrary, library));
        tracker.StartPath(emptyLibrary, Guid.NewGuid());
        tracker.End(emptyLibrary);
        Assert.Null(tracker.Get(library));
    }

    [Fact]
    public void Notifications_ObserveUpdatedSnapshotsAndIgnoreNoOpReports()
    {
        var tracker = new LibraryRefreshProgressTracker();
        var library = Guid.NewGuid();
        var path = Guid.NewGuid();
        var snapshots = new List<LibraryRefreshProgress?>();
        tracker.Changed += id => snapshots.Add(tracker.Get(id));

        var operation = tracker.Begin(Plan(library, path), aggregate: false);
        tracker.StartPath(operation, path);
        tracker.ReportPath(operation, path, 25);
        tracker.ReportPath(operation, path, 25);
        tracker.ReportPath(operation, path, 10);
        tracker.StartPath(operation, path);
        tracker.CompletePath(operation, path);
        tracker.CompletePath(operation, path);
        tracker.End(operation);

        Assert.Equal(5, snapshots.Count);
        Assert.Equal("Queued", snapshots[0]?.Status);
        Assert.Equal("Active", snapshots[1]?.Status);
        Assert.Equal(25, snapshots[2]?.Progress);
        Assert.Equal("Idle", snapshots[3]?.Status);
        Assert.Null(snapshots[4]);
        Assert.Equal(0, snapshots[0]?.Progress);
    }

    [Fact]
    public void Clear_RemovesAllOwnersAndNotifiesEachLibraryOnce()
    {
        var tracker = new LibraryRefreshProgressTracker();
        var firstLibrary = Guid.NewGuid();
        var secondLibrary = Guid.NewGuid();
        var path = Guid.NewGuid();
        var first = tracker.Begin(Plan(firstLibrary, path), aggregate: false);
        var second = tracker.Begin(
            new Dictionary<Guid, IReadOnlyList<Guid>>
            {
                [firstLibrary] = new[] { path },
                [secondLibrary] = new[] { path }
            },
            aggregate: true);
        tracker.StartPath(first, path);
        tracker.StartPath(second, path);
        var changed = new List<Guid>();
        tracker.Changed += changed.Add;

        tracker.Clear();
        Assert.Equal(2, changed.Count);
        Assert.Contains(firstLibrary, changed);
        Assert.Contains(secondLibrary, changed);
        Assert.Null(tracker.Get(firstLibrary));
        Assert.Null(tracker.Get(secondLibrary));
        Assert.Empty(tracker.GetPaths(first, firstLibrary));
        Assert.Empty(tracker.GetPaths(second, secondLibrary));
        tracker.ReportPath(first, path, 80);
        tracker.ReportPath(second, path, 80);
        tracker.Clear();
        Assert.Equal(2, changed.Count);
    }

    [Fact]
    public void ConcurrentReports_KeepEachPathMonotonicAndBounded()
    {
        var tracker = new LibraryRefreshProgressTracker();
        var library = Guid.NewGuid();
        var path = Guid.NewGuid();
        var operation = tracker.Begin(Plan(library, path), aggregate: true);
        tracker.StartPath(operation, path);

        Parallel.For(0, 151, percent => tracker.ReportPath(operation, path, percent));

        AssertSnapshot(tracker.Get(library), operation, "Aggregate", "Active", 100, 1, 1);
        tracker.CompletePath(operation, path);
        AssertSnapshot(tracker.Get(library), operation, "Aggregate", "Idle", 100, 1, 1);
    }

    private static Dictionary<Guid, IReadOnlyList<Guid>> Plan(Guid library, params Guid[] paths)
        => new() { [library] = paths };

    private static void AssertSnapshot(
        LibraryRefreshProgress? snapshot,
        Guid operation,
        string mode,
        string status,
        double progress,
        int current,
        int total)
    {
        Assert.NotNull(snapshot);
        Assert.Equal(operation, snapshot.OperationId);
        Assert.Equal(mode, snapshot.Mode);
        Assert.Equal(status, snapshot.Status);
        Assert.Equal(progress, snapshot.Progress, precision: 8);
        Assert.Equal(current, snapshot.CurrentPath);
        Assert.Equal(total, snapshot.TotalPaths);
    }
}
