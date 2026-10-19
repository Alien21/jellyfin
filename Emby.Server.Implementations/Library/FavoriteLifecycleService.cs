using System.Linq;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;

namespace Emby.Server.Implementations.Library;

/// <summary>
/// Synchronizes favorites across matching library copies.
/// </summary>
public sealed class FavoriteLifecycleService : IFavoriteLifecycleService
{
    private readonly object _gate = new();
    private readonly IServerConfigurationManager _configuration;
    private readonly IUserDataManager _userData;
    private readonly IFavoriteItemMatcher _matcher;

    /// <summary>
    /// Initializes a new instance of the <see cref="FavoriteLifecycleService"/> class.
    /// </summary>
    /// <param name="configuration">The server configuration.</param>
    /// <param name="userData">The user data manager.</param>
    /// <param name="matcher">The library copy matcher.</param>
    public FavoriteLifecycleService(
        IServerConfigurationManager configuration,
        IUserDataManager userData,
        IFavoriteItemMatcher matcher)
    {
        _configuration = configuration;
        _userData = userData;
        _matcher = matcher;
    }

    /// <inheritdoc />
    public void SetFavorite(User user, BaseItem item, bool isFavorite)
    {
        lock (_gate)
        {
            var supported = item is Movie or Episode or Series or Season;
            var copies = _configuration.Configuration.EnableFavoriteSyncAcrossLibraries && supported
                ? _matcher.GetEquivalentItems(user, item)
                : new[] { item };
            foreach (var copy in copies.DistinctBy(copy => copy.Id))
            {
                if ((_userData.GetUserData(user, copy)?.IsFavorite == true) == isFavorite)
                {
                    continue;
                }

                _userData.SaveUserData(user, copy, new UpdateUserItemDataDto { IsFavorite = isFavorite }, UserDataSaveReason.UpdateUserRating);
            }
        }
    }
}
