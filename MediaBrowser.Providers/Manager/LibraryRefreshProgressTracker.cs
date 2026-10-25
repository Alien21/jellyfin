using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Model.Entities;

namespace MediaBrowser.Providers.Manager;

/// <summary>
/// Tracks library refresh plans independently of overlapping refresh operations.
/// </summary>
public sealed class LibraryRefreshProgressTracker
{
    private readonly object _syncLock = new();
    private readonly Dictionary<Guid, RefreshOperation> _operations = new();
    private long _nextOperationSequence;

    /// <summary>
    /// Occurs after the progress associated with a library changes.
    /// </summary>
    public event Action<Guid>? Changed;

    /// <summary>
    /// Registers an immutable plan before any of its paths starts.
    /// </summary>
    /// <param name="libraries">The physical paths planned for each library.</param>
    /// <param name="aggregate">Whether percentages include every planned path.</param>
    /// <returns>The identifier owning this refresh plan.</returns>
    public Guid Begin(IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> libraries, bool aggregate)
    {
        ArgumentNullException.ThrowIfNull(libraries);
        var operation = new RefreshOperation { Id = Guid.NewGuid(), Aggregate = aggregate };
        foreach (var (libraryId, paths) in libraries)
        {
            ArgumentNullException.ThrowIfNull(paths);
            var distinct = new HashSet<Guid>();
            var planned = new List<Guid>();
            foreach (var pathId in paths)
            {
                if (!distinct.Add(pathId))
                {
                    continue;
                }

                planned.Add(pathId);
                if (!operation.Paths.TryGetValue(pathId, out var state))
                {
                    state = new PathState();
                    operation.Paths.Add(pathId, state);
                }

                state.Libraries.Add(libraryId);
            }

            operation.Libraries.Add(libraryId, planned.AsReadOnly());
        }

        lock (_syncLock)
        {
            operation.Sequence = ++_nextOperationSequence;
            _operations.Add(operation.Id, operation);
        }

        Notify(operation.Libraries.Keys);
        return operation.Id;
    }

    /// <summary>
    /// Gets the deterministic selected operation for a library.
    /// </summary>
    /// <param name="libraryId">The library identifier.</param>
    /// <returns>The selected immutable progress snapshot, or null when no plan remains.</returns>
    public LibraryRefreshProgress? Get(Guid libraryId)
    {
        lock (_syncLock)
        {
            LibraryRefreshProgress? selected = null;
            (int Status, int Mode, long Sequence) selectedPriority = default;
            foreach (var operation in _operations.Values)
            {
                if (!operation.Libraries.TryGetValue(libraryId, out var paths))
                {
                    continue;
                }

                var snapshot = CreateSnapshot(operation, paths);
                var statusPriority = snapshot.Status switch
                {
                    "Active" => 0,
                    "Queued" => 1,
                    _ => 2
                };
                var priority = (statusPriority, statusPriority == 0 && operation.Aggregate ? 0 : 1, operation.Sequence);
                if (selected is null || priority.CompareTo(selectedPriority) < 0)
                {
                    selected = snapshot;
                    selectedPriority = priority;
                }
            }

            return selected;
        }
    }

    /// <summary>
    /// Gets the stable path plan belonging to one library operation.
    /// </summary>
    /// <param name="operationId">The operation identifier.</param>
    /// <param name="libraryId">The library identifier.</param>
    /// <returns>The distinct planned paths, or an empty list for an unknown plan.</returns>
    public IReadOnlyList<Guid> GetPaths(Guid operationId, Guid libraryId)
    {
        lock (_syncLock)
        {
            return _operations.TryGetValue(operationId, out var operation)
                && operation.Libraries.TryGetValue(libraryId, out var paths)
                ? paths
                : Array.Empty<Guid>();
        }
    }

    /// <summary>
    /// Marks a planned path as started without resetting repeated start notifications.
    /// </summary>
    /// <param name="operationId">The operation identifier.</param>
    /// <param name="pathId">The physical path identifier.</param>
    public void StartPath(Guid operationId, Guid pathId)
    {
        Guid[] changed;
        lock (_syncLock)
        {
            if (!_operations.TryGetValue(operationId, out var operation)
                || !operation.Paths.TryGetValue(pathId, out var path)
                || path.Started)
            {
                return;
            }

            path.Started = true;
            path.StartSequence = ++operation.NextPathSequence;
            changed = path.Libraries.ToArray();
        }

        Notify(changed);
    }

    /// <summary>
    /// Advances a started path using a finite, clamped and nondecreasing percentage.
    /// </summary>
    /// <param name="operationId">The operation identifier.</param>
    /// <param name="pathId">The physical path identifier.</param>
    /// <param name="progress">The reported percentage.</param>
    public void ReportPath(Guid operationId, Guid pathId, double progress)
    {
        if (!double.IsFinite(progress))
        {
            return;
        }

        Guid[] changed;
        lock (_syncLock)
        {
            if (!_operations.TryGetValue(operationId, out var operation)
                || !operation.Paths.TryGetValue(pathId, out var path)
                || !path.Started
                || path.Completed)
            {
                return;
            }

            var clamped = Math.Clamp(progress, 0, 100);
            if (clamped <= path.Progress)
            {
                return;
            }

            path.Progress = clamped;
            changed = path.Libraries.ToArray();
        }

        Notify(changed);
    }

    /// <summary>
    /// Completes a started path while retaining its contribution to aggregate progress.
    /// </summary>
    /// <param name="operationId">The operation identifier.</param>
    /// <param name="pathId">The physical path identifier.</param>
    public void CompletePath(Guid operationId, Guid pathId)
    {
        Guid[] changed;
        lock (_syncLock)
        {
            if (!_operations.TryGetValue(operationId, out var operation)
                || !operation.Paths.TryGetValue(pathId, out var path)
                || !path.Started
                || path.Completed)
            {
                return;
            }

            path.Completed = true;
            path.Progress = 100;
            changed = path.Libraries.ToArray();
        }

        Notify(changed);
    }

    /// <summary>
    /// Removes only the state owned by the completed or cancelled operation.
    /// </summary>
    /// <param name="operationId">The operation identifier.</param>
    public void End(Guid operationId)
    {
        Guid[] changed;
        lock (_syncLock)
        {
            if (!_operations.Remove(operationId, out var operation))
            {
                return;
            }

            changed = operation.Libraries.Keys.ToArray();
        }

        Notify(changed);
    }

    /// <summary>
    /// Removes every remaining operation during shutdown.
    /// </summary>
    public void Clear()
    {
        Guid[] changed;
        lock (_syncLock)
        {
            changed = _operations.Values.SelectMany(operation => operation.Libraries.Keys).Distinct().ToArray();
            _operations.Clear();
        }

        Notify(changed);
    }

    private static LibraryRefreshProgress CreateSnapshot(RefreshOperation operation, IReadOnlyList<Guid> paths)
    {
        var started = 0;
        var completed = 0;
        var currentPath = 0;
        var currentProgress = 0.0;
        var totalProgress = 0.0;
        var latestStart = 0L;
        for (var index = 0; index < paths.Count; index++)
        {
            var path = operation.Paths[paths[index]];
            totalProgress += path.Progress;
            if (path.Completed)
            {
                completed++;
            }

            if (path.Started)
            {
                started++;
                if (path.StartSequence > latestStart)
                {
                    latestStart = path.StartSequence;
                    currentPath = index + 1;
                    currentProgress = path.Progress;
                }
            }
        }

        return new LibraryRefreshProgress
        {
            OperationId = operation.Id,
            Mode = operation.Aggregate ? "Aggregate" : "Sequential",
            Status = completed == paths.Count ? "Idle" : started == 0 ? "Queued" : "Active",
            Progress = operation.Aggregate && paths.Count > 0 ? totalProgress / paths.Count : currentProgress,
            CurrentPath = operation.Aggregate ? started : currentPath,
            TotalPaths = paths.Count
        };
    }

    private void Notify(IEnumerable<Guid> libraries)
    {
        var handler = Changed;
        foreach (var libraryId in libraries)
        {
            handler?.Invoke(libraryId);
        }
    }

    private sealed class RefreshOperation
    {
        public Guid Id { get; init; }

        public bool Aggregate { get; init; }

        public long Sequence { get; set; }

        public long NextPathSequence { get; set; }

        public Dictionary<Guid, IReadOnlyList<Guid>> Libraries { get; } = new();

        public Dictionary<Guid, PathState> Paths { get; } = new();
    }

    private sealed class PathState
    {
        public List<Guid> Libraries { get; } = new();

        public bool Started { get; set; }

        public bool Completed { get; set; }

        public long StartSequence { get; set; }

        public double Progress { get; set; }
    }
}
