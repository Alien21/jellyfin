using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;

namespace MediaBrowser.Controller.Library;

/// <summary>
/// Coordinates favorites across library copies and records new viewing after favoriting.
/// </summary>
public interface IFavoriteLifecycleService
{
    /// <summary>
    /// Sets favorite status and starts or cancels the corresponding viewing cycle.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="item">The item.</param>
    /// <param name="isFavorite">The requested favorite status.</param>
    void SetFavorite(User user, BaseItem item, bool isFavorite);

    /// <summary>
    /// Records the first completion of a playback session, including for items not currently favorited.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="item">The actual played item.</param>
    /// <param name="playSessionId">The stable playback session identifier.</param>
    void RecordPlaybackCompletion(User user, BaseItem item, string? playSessionId);

    /// <summary>
    /// Records an explicit mark-played action, even if the item was already played.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="item">The explicitly marked item, including an entire series or season.</param>
    void RecordMarkedPlayed(User user, BaseItem item);
}
