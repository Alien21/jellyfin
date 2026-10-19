using System.Collections.Generic;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;

namespace MediaBrowser.Controller.Library;

/// <summary>
/// Identifies copies of favorite movies and television items without merging library records.
/// </summary>
public interface IFavoriteItemMatcher
{
    /// <summary>
    /// Gets an item and its equivalent copies in the user's accessible libraries of the same kind.
    /// </summary>
    /// <param name="user">The user whose library access applies.</param>
    /// <param name="item">The source item.</param>
    /// <returns>The source item and its equivalent copies, with no duplicate item IDs.</returns>
    IReadOnlyList<BaseItem> GetEquivalentItems(User user, BaseItem item);

    /// <summary>
    /// Gets a typed metadata identity, or an item-specific identity when metadata is incomplete.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <returns>The item's logical identity.</returns>
    string GetIdentityKey(BaseItem item);

    /// <summary>
    /// Gets distinct, available logical episodes from a series or season and its accessible copies.
    /// </summary>
    /// <param name="user">The user whose library access applies.</param>
    /// <param name="container">The series or season.</param>
    /// <returns>One representative of each available episode, including distinct combined-episode ranges.</returns>
    IReadOnlyList<Episode> GetEpisodes(User user, BaseItem container);
}
