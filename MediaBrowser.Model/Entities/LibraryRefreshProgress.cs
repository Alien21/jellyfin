using System;

namespace MediaBrowser.Model.Entities;

/// <summary>
/// Describes the selected refresh operation for a media library.
/// </summary>
public sealed class LibraryRefreshProgress
{
    /// <summary>
    /// Gets the identifier of the refresh operation.
    /// </summary>
    public Guid OperationId { get; init; }

    /// <summary>
    /// Gets whether progress represents sequential paths or all planned paths.
    /// </summary>
    public string Mode { get; init; } = "Sequential";

    /// <summary>
    /// Gets the queued, active or idle state of the operation.
    /// </summary>
    public string Status { get; init; } = "Idle";

    /// <summary>
    /// Gets the current path percentage or the aggregate percentage of all paths.
    /// </summary>
    public double Progress { get; init; }

    /// <summary>
    /// Gets the current sequential path position or the number of started aggregate paths.
    /// </summary>
    public int CurrentPath { get; init; }

    /// <summary>
    /// Gets the distinct number of paths planned for this library operation.
    /// </summary>
    public int TotalPaths { get; init; }
}
