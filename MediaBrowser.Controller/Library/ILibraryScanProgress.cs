using System;
using System.Collections.Generic;

namespace MediaBrowser.Controller.Library;

/// <summary>
/// Observes the existing validation and metadata work for a planned library scan.
/// </summary>
public interface ILibraryScanProgress : IProgress<double>
{
    /// <summary>
    /// Marks a planned physical path as started.
    /// </summary>
    /// <param name="pathId">The physical folder identifier.</param>
    void StartPath(Guid pathId);

    /// <summary>
    /// Reports a physical path's progress across the scan phases.
    /// </summary>
    /// <param name="pathId">The physical folder identifier.</param>
    /// <param name="progress">The percentage completed.</param>
    void ReportPath(Guid pathId, double progress);

    /// <summary>
    /// Marks a planned path as finished.
    /// </summary>
    /// <param name="pathId">The physical folder identifier.</param>
    void CompletePath(Guid pathId);

    /// <summary>
    /// Finishes planned paths excluded from the scan's accessible children.
    /// </summary>
    /// <param name="accessiblePaths">The physical folders that will be processed.</param>
    void CompleteSkippedPaths(IEnumerable<Guid> accessiblePaths);
}
