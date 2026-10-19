using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;

namespace MediaBrowser.Controller.Library;

/// <summary>
/// Coordinates favorites across matching library copies.
/// </summary>
public interface IFavoriteLifecycleService
{
    /// <summary>
    /// Sets favorite status on the item and its matching copies when synchronization is enabled.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="item">The item.</param>
    /// <param name="isFavorite">The requested favorite status.</param>
    void SetFavorite(User user, BaseItem item, bool isFavorite);
}
