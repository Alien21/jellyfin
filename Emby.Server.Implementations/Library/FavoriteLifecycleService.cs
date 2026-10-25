using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.Library;

/// <summary>
/// Synchronizes library copies without sharing their playback positions, and tracks new viewing of favorites.
/// </summary>
public sealed class FavoriteLifecycleService : IFavoriteLifecycleService
{
    private readonly object _gate = new();
    private readonly IServerConfigurationManager _configuration;
    private readonly ILibraryManager _library;
    private readonly IUserDataManager _userData;
    private readonly IFavoriteItemMatcher _matcher;
    private readonly ILogger<FavoriteLifecycleService> _logger;
    private readonly FavoriteLifecycleStore _store;
    private readonly FavoriteState _state;
    private string? _pendingSessionKey;

    /// <summary>
    /// Initializes a new instance of the <see cref="FavoriteLifecycleService"/> class.
    /// </summary>
    /// <param name="configuration">The configuration manager.</param>
    /// <param name="library">The library manager.</param>
    /// <param name="userData">The user data manager.</param>
    /// <param name="matcher">The library copy matcher.</param>
    /// <param name="logger">The logger.</param>
    public FavoriteLifecycleService(
        IServerConfigurationManager configuration,
        ILibraryManager library,
        IUserDataManager userData,
        IFavoriteItemMatcher matcher,
        ILogger<FavoriteLifecycleService> logger)
    {
        _configuration = configuration;
        _library = library;
        _userData = userData;
        _matcher = matcher;
        _logger = logger;
        _store = new FavoriteLifecycleStore(configuration.ApplicationPaths.DataPath);
        _state = LoadState();
    }

    /// <inheritdoc />
    public void SetFavorite(User user, BaseItem item, bool isFavorite)
    {
        lock (_gate)
        {
            if (!_configuration.Configuration.EnableFavoriteSyncAcrossLibraries
                && !_configuration.Configuration.EnableAutoRemoveFavoritesAfterWatching
                && _state.Cycles.Count == 0)
            {
                WriteFavorite(user, item, isFavorite);
                return;
            }

            var supported = IsSupported(item);
            var copies = supported ? _matcher.GetEquivalentItems(user, item) : new[] { item };
            var ids = copies.Select(copy => copy.Id).ToHashSet();
            var previouslyFavorite = copies.Any(copy => IsFavorite(user, copy));

            // A new explicit favorite overrides a removal that was interrupted while writing
            // separate copies. Persist the cancellation before restoring any favorite rows.
            var cycleRemoved = isFavorite && _state.Cycles.RemoveAll(cycle =>
                cycle.UserId.Equals(user.Id) && cycle.Completed && cycle.ItemIds.Overlaps(ids)) > 0;
            if (cycleRemoved)
            {
                SaveState();
            }

            foreach (var copy in _configuration.Configuration.EnableFavoriteSyncAcrossLibraries && supported ? copies : new[] { item })
            {
                WriteFavorite(user, copy, isFavorite);
            }

            if (!previouslyFavorite || !copies.Any(copy => IsFavorite(user, copy)))
            {
                cycleRemoved |= _state.Cycles.RemoveAll(cycle => cycle.UserId.Equals(user.Id) && cycle.ItemIds.Overlaps(ids)) > 0;
            }

            if (supported && isFavorite && _configuration.Configuration.EnableAutoRemoveFavoritesAfterWatching)
            {
                EnsureCycle(user, item, copies);
            }

            if (cycleRemoved || _configuration.Configuration.EnableAutoRemoveFavoritesAfterWatching)
            {
                SaveState();
            }
        }
    }

    /// <inheritdoc />
    public void RecordPlaybackCompletion(User user, BaseItem item, string? playSessionId)
    {
        if (!_configuration.Configuration.EnableAutoRemoveFavoritesAfterWatching
            || (item is not Movie && item is not Episode)
            || string.IsNullOrWhiteSpace(playSessionId))
        {
            return;
        }

        lock (_gate)
        {
            // Retry persisted removals before deduplication, including a retry of the same
            // playback session whose earlier removal was interrupted.
            ResumePendingRemovals(user);
            var sessionKey = $"{user.Id:N}/{item.Id:N}/{playSessionId}";
            if (_store.HasCompletedSession(sessionKey))
            {
                return;
            }

            RecordViewed(user, item, resumePending: false, completedSessionKey: sessionKey);
        }
    }

    /// <inheritdoc />
    public void RecordMarkedPlayed(User user, BaseItem item)
    {
        if (!_configuration.Configuration.EnableAutoRemoveFavoritesAfterWatching || !IsSupported(item))
        {
            return;
        }

        lock (_gate)
        {
            RecordViewed(user, item);
        }
    }

    private static bool IsSupported(BaseItem item) => item is Movie or Episode or Series or Season or BoxSet;

    private bool IsFavorite(User user, BaseItem item) => _userData.GetUserData(user, item)?.IsFavorite == true;

    private bool WriteFavorite(User user, BaseItem item, bool value)
    {
        if (IsFavorite(user, item) == value)
        {
            return false;
        }

        _userData.SaveUserData(user, item, new UpdateUserItemDataDto { IsFavorite = value }, UserDataSaveReason.UpdateUserRating);
        return true;
    }

    private IReadOnlyList<BaseItem> GetFavorites(User user) => _library.GetItemList(new InternalItemsQuery(user)
    {
        IsFavorite = true,
        IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.Series, BaseItemKind.Season, BaseItemKind.BoxSet },
        IsVirtualItem = false,
        Recursive = true,
        GroupByPresentationUniqueKey = false,
        IncludeAlternateVersions = true
    });

    private FavoriteCycle EnsureCycle(User user, BaseItem item, IReadOnlyList<BaseItem>? knownCopies = null)
    {
        var copies = knownCopies ?? _matcher.GetEquivalentItems(user, item);
        var ids = copies.Select(copy => copy.Id).ToHashSet();
        var cycle = _state.Cycles.FirstOrDefault(entry => entry.UserId.Equals(user.Id) && entry.ItemIds.Overlaps(ids));
        if (cycle is null)
        {
            cycle = new FavoriteCycle { UserId = user.Id, IsContainer = item is Series or Season or BoxSet };
            _state.Cycles.Add(cycle);
        }

        cycle.ItemIds.UnionWith(ids);
        if (item is BoxSet collection)
        {
            RefreshCollectionUnits(user, collection, cycle);
            return cycle;
        }

        if (!cycle.IsContainer && item is not Episode)
        {
            return cycle;
        }

        // Rebuild the required logical episodes on relevant viewing events. This includes
        // new episodes and drops deleted ones without losing viewing already recorded.
        var previous = cycle.Units.ToArray();
        var numberedPrevious = previous
            .Where(unit => unit.SeasonNumber.HasValue && unit.EpisodeNumber.HasValue)
            .GroupBy(unit => (unit.SeasonNumber!.Value, unit.EpisodeNumber!.Value))
            .ToDictionary(group => group.Key, group => group.ToArray());
        var fallbackPrevious = previous
            .Where(unit => !unit.SeasonNumber.HasValue || !unit.EpisodeNumber.HasValue)
            .ToArray();
        var units = new List<WatchUnit>();
        var numberedUnits = new Dictionary<(int Season, int Episode), WatchUnit>();
        cycle.SeriesIds = GetSeriesIds(user, copies);

        var requiredEpisodes = item is Episode favoriteEpisode ? new[] { favoriteEpisode } : _matcher.GetEpisodes(user, item);
        foreach (var episode in requiredEpisodes)
        {
            if (!episode.SeriesId.Equals(Guid.Empty))
            {
                cycle.SeriesIds.Add(episode.SeriesId);
            }

            if (episode.ParentIndexNumber.HasValue && episode.IndexNumber.HasValue)
            {
                var episodeIds = fallbackPrevious.Length > 0
                    ? _matcher.GetEquivalentItems(user, episode).Select(copy => copy.Id).ToHashSet()
                    : new HashSet<Guid> { episode.Id };
                episodeIds.Add(episode.Id);
                var last = Math.Max(episode.IndexNumber.Value, episode.IndexNumberEnd ?? episode.IndexNumber.Value);
                for (var number = episode.IndexNumber.Value; ; number++)
                {
                    var key = (episode.ParentIndexNumber.Value, number);
                    if (!numberedUnits.TryGetValue(key, out var unit))
                    {
                        unit = new WatchUnit { SeasonNumber = episode.ParentIndexNumber.Value, EpisodeNumber = number };
                        numberedUnits.Add(key, unit);
                        units.Add(unit);
                    }

                    if (numberedPrevious.TryGetValue(key, out var aliases))
                    {
                        foreach (var alias in aliases)
                        {
                            unit.Completed |= alias.Completed;
                            unit.ItemIds.UnionWith(alias.ItemIds);
                        }
                    }

                    foreach (var alias in fallbackPrevious.Where(alias => alias.ItemIds.Overlaps(episodeIds)))
                    {
                        unit.Completed |= alias.Completed;
                        unit.ItemIds.UnionWith(alias.ItemIds);
                    }

                    unit.ItemIds.UnionWith(episodeIds);
                    if (number == last)
                    {
                        break;
                    }
                }
            }
            else
            {
                var episodeIds = _matcher.GetEquivalentItems(user, episode).Select(copy => copy.Id).ToHashSet();
                episodeIds.Add(episode.Id);
                var unit = units.FirstOrDefault(entry => entry.ItemIds.Overlaps(episodeIds));
                if (unit is null)
                {
                    unit = new WatchUnit();
                    units.Add(unit);
                }

                foreach (var alias in previous.Where(alias => alias.ItemIds.Overlaps(episodeIds)))
                {
                    unit.Completed |= alias.Completed;
                    unit.ItemIds.UnionWith(alias.ItemIds);
                }

                unit.ItemIds.UnionWith(episodeIds);
            }
        }

        cycle.Units = units;
        return cycle;
    }

    private void RefreshCollectionUnits(User user, BoxSet collection, FavoriteCycle cycle)
    {
        var previous = cycle.Units;
        var units = new List<WatchUnit>();
        var members = _matcher.GetCollectionItems(user, collection);
        cycle.SeriesIds = GetSeriesIds(user, members);
        foreach (var member in members)
        {
            var copies = _matcher.GetEquivalentItems(user, member);
            var ids = copies.Select(copy => copy.Id).ToHashSet();
            ids.Add(member.Id);
            var seriesIds = GetSeriesIds(user, copies);
            if (member is Episode episode && episode.ParentIndexNumber.HasValue && episode.IndexNumber.HasValue && seriesIds.Count > 0)
            {
                var last = Math.Max(episode.IndexNumber.Value, episode.IndexNumberEnd ?? episode.IndexNumber.Value);
                for (var number = episode.IndexNumber.Value; ; number++)
                {
                    var episodeUnit = units.FirstOrDefault(unit => unit.SeasonNumber == episode.ParentIndexNumber
                        && unit.EpisodeNumber == number && unit.SeriesIds.Overlaps(seriesIds));
                    if (episodeUnit is null)
                    {
                        episodeUnit = new WatchUnit { SeasonNumber = episode.ParentIndexNumber, EpisodeNumber = number };
                        units.Add(episodeUnit);
                    }

                    episodeUnit.SeriesIds.UnionWith(seriesIds);
                    episodeUnit.ItemIds.UnionWith(ids);
                    foreach (var alias in previous.Where(unit => unit.SeasonNumber == episode.ParentIndexNumber
                        && unit.EpisodeNumber == number && unit.SeriesIds.Overlaps(seriesIds)))
                    {
                        episodeUnit.Completed |= alias.Completed;
                        episodeUnit.ItemIds.UnionWith(alias.ItemIds);
                    }

                    if (number == last)
                    {
                        break;
                    }
                }

                continue;
            }

            var unit = new WatchUnit { ItemIds = ids };

            // One viewing of a film qualifies for all of its library copies. Preserve a
            // completed logical member when a collection gains or loses another copy.
            foreach (var alias in previous.Where(alias => alias.ItemIds.Overlaps(ids)))
            {
                unit.Completed |= alias.Completed;
                unit.ItemIds.UnionWith(alias.ItemIds);
            }

            foreach (var duplicate in units.Where(existing => existing.ItemIds.Overlaps(unit.ItemIds)).ToArray())
            {
                unit.Completed |= duplicate.Completed;
                unit.ItemIds.UnionWith(duplicate.ItemIds);
                units.Remove(duplicate);
            }

            units.Add(unit);
        }

        cycle.Units = units;
    }

    private HashSet<Guid> GetSeriesIds(User user, IEnumerable<BaseItem> items)
    {
        var ids = new HashSet<Guid>();
        foreach (var item in items)
        {
            if (item is Series series)
            {
                ids.Add(series.Id);
                continue;
            }

            var seriesId = item switch
            {
                Season season => season.SeriesId,
                Episode episode => episode.SeriesId,
                _ => Guid.Empty
            };
            if (seriesId.Equals(Guid.Empty) || !ids.Add(seriesId))
            {
                continue;
            }

            if (_library.GetItemById<BaseItem>(seriesId, user) is Series parent)
            {
                ids.UnionWith(_matcher.GetEquivalentItems(user, parent).OfType<Series>().Select(copy => copy.Id));
            }
        }

        return ids;
    }

    private void RecordViewed(User user, BaseItem item, bool resumePending = true, string? completedSessionKey = null)
    {
        if (resumePending)
        {
            ResumePendingRemovals(user);
        }

        var favorites = GetFavorites(user);
        _state.Cycles.RemoveAll(cycle => cycle.UserId.Equals(user.Id)
            && !favorites.Any(favorite => cycle.ItemIds.Contains(favorite.Id) && IsFavorite(user, favorite)));

        var copies = _matcher.GetEquivalentItems(user, item);
        var directIds = copies.Select(copy => copy.Id).ToHashSet();
        var collectionItems = item is BoxSet collection ? _matcher.GetCollectionItems(user, collection) : Array.Empty<BaseItem>();
        var episodes = item is Series or Season ? _matcher.GetEpisodes(user, item)
            : item is Episode episode ? new[] { episode } : collectionItems.OfType<Episode>().ToArray();
        var viewedEpisodes = episodes.Select(viewedEpisode => new
        {
            Episode = viewedEpisode,
            Ids = _matcher.GetEquivalentItems(user, viewedEpisode).Select(copy => copy.Id).ToHashSet()
        }).ToArray();
        var viewedMovieIds = collectionItems.OfType<Movie>()
            .SelectMany(movie => _matcher.GetEquivalentItems(user, movie).Select(copy => copy.Id));
        var viewedIds = viewedEpisodes.SelectMany(viewing => viewing.Ids).Concat(viewedMovieIds).Concat(directIds).ToHashSet();
        var seriesIds = GetSeriesIds(user, copies.Concat(episodes));

        // Initialize and refresh only favorites related to this viewing event. An unrelated
        // movie completion must not enumerate every favorite series' episodes.
        foreach (var favorite in favorites)
        {
            var existing = _state.Cycles.FirstOrDefault(cycle => cycle.UserId.Equals(user.Id) && cycle.ItemIds.Contains(favorite.Id));
            var related = viewedIds.Contains(favorite.Id)
                // Refresh collection membership even for an existing favorite that has not
                // been tracked yet, or a film that was added after its previous viewing.
                || favorite is BoxSet
                || (favorite is Series series && seriesIds.Contains(series.Id))
                || (favorite is Season season && seriesIds.Contains(season.SeriesId))
                || (favorite is Episode favoriteEpisode
                    && favoriteEpisode.ParentIndexNumber.HasValue
                    && favoriteEpisode.IndexNumber.HasValue
                    && seriesIds.Contains(favoriteEpisode.SeriesId)
                    && viewedEpisodes.Any(viewing => viewing.Episode.ParentIndexNumber == favoriteEpisode.ParentIndexNumber
                        && viewing.Episode.IndexNumber.HasValue
                        && viewing.Episode.IndexNumber.Value <= (favoriteEpisode.IndexNumberEnd ?? favoriteEpisode.IndexNumber.Value)
                        && favoriteEpisode.IndexNumber.Value <= (viewing.Episode.IndexNumberEnd ?? viewing.Episode.IndexNumber.Value)))
                || (viewedEpisodes.Length > 0 && existing is not null && existing.Units.Any(unit => unit.ItemIds.Overlaps(viewedIds)));
            if (related && IsFavorite(user, favorite))
            {
                EnsureCycle(user, favorite);
            }
        }

        foreach (var cycle in _state.Cycles.Where(entry => entry.UserId.Equals(user.Id)).ToArray())
        {
            if (!favorites.Any(favorite => cycle.ItemIds.Contains(favorite.Id) && IsFavorite(user, favorite)))
            {
                _state.Cycles.Remove(cycle);
                continue;
            }

            if (cycle.ItemIds.Overlaps(directIds))
            {
                cycle.Completed = true;
                continue;
            }

            if (!cycle.IsContainer && cycle.Units.Count == 0)
            {
                if (cycle.ItemIds.Overlaps(viewedIds))
                {
                    cycle.Completed = true;
                }

                continue;
            }

            if (viewedEpisodes.Length == 0 && item is not Movie && collectionItems.Count == 0)
            {
                continue;
            }

            if (!cycle.SeriesIds.Overlaps(seriesIds) && !cycle.Units.Any(unit => unit.ItemIds.Overlaps(viewedIds)))
            {
                continue;
            }

            foreach (var unit in cycle.Units.Where(unit => !unit.Completed))
            {
                unit.Completed = unit.ItemIds.Overlaps(viewedIds) || viewedEpisodes.Any(viewing => unit.ItemIds.Overlaps(viewing.Ids)
                    || (unit.EpisodeNumber.HasValue
                        && (unit.SeriesIds.Count > 0 ? unit.SeriesIds : cycle.SeriesIds).Contains(viewing.Episode.SeriesId)
                        && unit.SeasonNumber == viewing.Episode.ParentIndexNumber
                        && viewing.Episode.IndexNumber.HasValue
                        && unit.EpisodeNumber >= viewing.Episode.IndexNumber
                        && unit.EpisodeNumber <= (viewing.Episode.IndexNumberEnd ?? viewing.Episode.IndexNumber)));
            }

            if (cycle.Units.Count > 0 && cycle.Units.All(unit => unit.Completed))
            {
                cycle.Completed = true;
            }
        }

        // Commit every affected cycle together with completion deduplication before removing
        // any favorite rows. A crash cannot consume this event while losing another cycle's progress.
        var completed = _state.Cycles.Where(cycle => cycle.UserId.Equals(user.Id) && cycle.Completed).ToArray();
        if (completedSessionKey is not null)
        {
            _pendingSessionKey = completedSessionKey;
        }

        SaveState();
        foreach (var cycle in completed)
        {
            FinishCycle(user, cycle);
        }

        if (completed.Length > 0)
        {
            SaveState();
        }
    }

    private void ResumePendingRemovals(User user)
    {
        // A previous state transaction may have failed before its session marker was committed.
        if (_pendingSessionKey is not null)
        {
            SaveState();
        }

        var pending = _state.Cycles.Where(cycle => cycle.UserId.Equals(user.Id) && cycle.Completed).ToArray();
        foreach (var cycle in pending)
        {
            FinishCycle(user, cycle);
        }

        if (pending.Length > 0)
        {
            SaveState();
        }
    }

    private void FinishCycle(User user, FavoriteCycle cycle)
    {
        var copies = cycle.ItemIds.Select(id => _library.GetItemById<BaseItem>(id, user)).OfType<BaseItem>().ToList();
        if (copies.Count > 0)
        {
            copies.AddRange(_matcher.GetEquivalentItems(user, copies[0]));
        }

        foreach (var copy in copies.DistinctBy(copy => copy.Id))
        {
            WriteFavorite(user, copy, false);
        }

        _state.Cycles.Remove(cycle);
    }

    private FavoriteState LoadState()
    {
        try
        {
            var json = _store.LoadState();
            return json is null ? new FavoriteState() : JsonSerializer.Deserialize<FavoriteState>(json) ?? new FavoriteState();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogError(exception, "Could not load favorite viewing cycles");
            throw;
        }
    }

    private void SaveState()
    {
        _store.SaveState(JsonSerializer.Serialize(_state), _pendingSessionKey);
        _pendingSessionKey = null;
    }

    private sealed class FavoriteState
    {
        public FavoriteState()
        {
        }

        public List<FavoriteCycle> Cycles { get; set; } = new();
    }

    private sealed class FavoriteCycle
    {
        public FavoriteCycle()
        {
        }

        public Guid UserId { get; set; }

        public HashSet<Guid> ItemIds { get; set; } = new();

        public bool IsContainer { get; set; }

        public HashSet<Guid> SeriesIds { get; set; } = new();

        public List<WatchUnit> Units { get; set; } = new();

        public bool Completed { get; set; }
    }

    private sealed class WatchUnit
    {
        public WatchUnit()
        {
        }

        public HashSet<Guid> ItemIds { get; set; } = new();

        public int? SeasonNumber { get; set; }

        public int? EpisodeNumber { get; set; }

        public HashSet<Guid> SeriesIds { get; set; } = new();

        public bool Completed { get; set; }
    }
}
