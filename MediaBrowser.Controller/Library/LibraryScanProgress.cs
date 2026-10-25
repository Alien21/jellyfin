using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace MediaBrowser.Controller.Library;

/// <summary>
/// Forwards overall scan progress and observes its frozen set of physical paths.
/// </summary>
public sealed class LibraryScanProgress : ILibraryScanProgress
{
    private readonly IProgress<double> _progress;
    private readonly HashSet<Guid> _paths;
    private readonly Action<Guid> _startPath;
    private readonly Action<Guid, double> _reportPath;
    private readonly Action<Guid> _completePath;
    private readonly ConcurrentDictionary<Guid, byte> _started = new();
    private readonly ConcurrentDictionary<Guid, byte> _completed = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryScanProgress"/> class.
    /// </summary>
    /// <param name="progress">The existing overall task progress.</param>
    /// <param name="paths">The planned physical folder identifiers.</param>
    /// <param name="startPath">The callback for a path's first phase.</param>
    /// <param name="reportPath">The callback for path progress.</param>
    /// <param name="completePath">The callback for terminal path progress.</param>
    public LibraryScanProgress(
        IProgress<double> progress,
        IEnumerable<Guid> paths,
        Action<Guid> startPath,
        Action<Guid, double> reportPath,
        Action<Guid> completePath)
    {
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(startPath);
        ArgumentNullException.ThrowIfNull(reportPath);
        ArgumentNullException.ThrowIfNull(completePath);
        _progress = progress;
        _paths = paths.ToHashSet();
        _startPath = startPath;
        _reportPath = reportPath;
        _completePath = completePath;
    }

    /// <inheritdoc />
    public void Report(double value) => _progress.Report(value);

    /// <inheritdoc />
    public void StartPath(Guid pathId)
    {
        if (_paths.Contains(pathId) && !_completed.ContainsKey(pathId) && _started.TryAdd(pathId, 0))
        {
            _startPath(pathId);
        }
    }

    /// <inheritdoc />
    public void ReportPath(Guid pathId, double progress)
    {
        if (_paths.Contains(pathId) && !_completed.ContainsKey(pathId))
        {
            _reportPath(pathId, progress);
        }
    }

    /// <inheritdoc />
    public void CompletePath(Guid pathId)
    {
        if (_paths.Contains(pathId) && _completed.TryAdd(pathId, 0))
        {
            _completePath(pathId);
        }
    }

    /// <inheritdoc />
    public void CompleteSkippedPaths(IEnumerable<Guid> accessiblePaths)
    {
        ArgumentNullException.ThrowIfNull(accessiblePaths);
        var accessible = accessiblePaths.ToHashSet();
        foreach (var path in _paths)
        {
            if (!accessible.Contains(path))
            {
                StartPath(path);
                CompletePath(path);
            }
        }
    }
}
