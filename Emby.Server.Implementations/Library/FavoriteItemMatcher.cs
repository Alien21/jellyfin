using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Querying;

namespace Emby.Server.Implementations.Library;

/// <summary>
/// Matches favorite items by their metadata while preserving separate library records.
/// </summary>
public sealed class FavoriteItemMatcher : IFavoriteItemMatcher
{
    private static readonly string[] _movieProviders = ["Tmdb", "Imdb", "Tvdb"];
    private static readonly string[] _seriesProviders = ["Tvdb", "Tmdb", "Imdb"];
    private readonly ILibraryManager _libraryManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="FavoriteItemMatcher"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    public FavoriteItemMatcher(ILibraryManager libraryManager)
    {
        _libraryManager = libraryManager;
    }

    /// <inheritdoc />
    public IReadOnlyList<BaseItem> GetEquivalentItems(User user, BaseItem item)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(item);

        if (!IsSupported(item) || !IsInExpectedLibrary(user, item))
        {
            return [item];
        }

        if (item is Movie or Series)
        {
            return GetProviderMatches(user, item);
        }

        var series = GetSeries(item);
        if (series is null)
        {
            return [item];
        }

        var seriesIds = GetEquivalentItems(user, series).Select(match => match.Id).ToHashSet();
        var query = CreateQuery(user, item.GetBaseItemKind());
        query.AncestorIds = seriesIds.ToArray();

        if (item is Season season && season.IndexNumber.HasValue)
        {
            query.IndexNumber = season.IndexNumber;
            return IncludeSource(item, _libraryManager.GetItemList(query)
                .OfType<Season>()
                .Where(candidate => candidate.IndexNumber == season.IndexNumber
                    && seriesIds.Contains(candidate.SeriesId)
                    && IsInExpectedLibrary(user, candidate)));
        }

        if (item is Episode episode && episode.ParentIndexNumber.HasValue && episode.IndexNumber.HasValue)
        {
            query.ParentIndexNumber = episode.ParentIndexNumber;
            query.IndexNumber = episode.IndexNumber;
            query.IsVirtualItem = false;
            return IncludeSource(item, _libraryManager.GetItemList(query)
                .OfType<Episode>()
                .Where(candidate => seriesIds.Contains(candidate.SeriesId)
                    && candidate.ParentIndexNumber == episode.ParentIndexNumber
                    && candidate.IndexNumber == episode.IndexNumber
                    && GetEpisodeEnd(candidate) == GetEpisodeEnd(episode)
                    && !candidate.IsVirtualItem
                    && IsInExpectedLibrary(user, candidate)));
        }

        return [item];
    }

    /// <inheritdoc />
    public string GetIdentityKey(BaseItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (item is Episode episode && episode.ParentIndexNumber.HasValue && episode.IndexNumber.HasValue)
        {
            var series = GetSeries(episode);
            if (series is not null)
            {
                return string.Create(CultureInfo.InvariantCulture, $"{GetIdentityKey(series)}/season:{episode.ParentIndexNumber}/episode:{episode.IndexNumber}-{GetEpisodeEnd(episode)}");
            }
        }

        if (item is Season season && season.IndexNumber.HasValue)
        {
            var series = GetSeries(season);
            if (series is not null)
            {
                return string.Create(CultureInfo.InvariantCulture, $"{GetIdentityKey(series)}/season:{season.IndexNumber}");
            }
        }

        if (item is Movie or Series)
        {
            foreach (var provider in GetProviders(item))
            {
                var value = GetProviderValue(item, provider);
                if (value is not null)
                {
                    return $"{item.GetBaseItemKind()}:{provider}:{value.ToLowerInvariant()}";
                }
            }
        }

        return $"{item.GetBaseItemKind()}:item:{item.Id:N}";
    }

    /// <inheritdoc />
    public IReadOnlyList<Episode> GetEpisodes(User user, BaseItem container)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(container);

        if (container is not (Series or Season))
        {
            return [];
        }

        var containers = GetEquivalentItems(user, container);
        var seriesIds = containers.Select(GetSeries).OfType<Series>().Select(series => series.Id).ToHashSet();
        var query = CreateQuery(user, BaseItemKind.Episode);
        query.AncestorIds = containers.Select(item => item.Id).ToArray();
        query.IsVirtualItem = false;

        // All queried containers already belong to one matched series. Numbered episodes therefore
        // share one identity even when a library copy has fewer provider IDs than another copy.
        return _libraryManager.GetItemList(query)
            .OfType<Episode>()
            .Where(episode => !episode.IsVirtualItem && seriesIds.Contains(episode.SeriesId) && IsInExpectedLibrary(user, episode)
                && (container is not Season season || !season.IndexNumber.HasValue || episode.ParentIndexNumber == season.IndexNumber))
            .OrderBy(episode => episode.Id)
            .DistinctBy(episode => episode.ParentIndexNumber.HasValue && episode.IndexNumber.HasValue
                ? string.Create(CultureInfo.InvariantCulture, $"{episode.ParentIndexNumber}:{episode.IndexNumber}:{GetEpisodeEnd(episode)}")
                : GetIdentityKey(episode))
            .ToList();
    }

    /// <inheritdoc />
    public IReadOnlyList<BaseItem> GetCollectionItems(User user, BoxSet collection)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(collection);

        var pending = new Queue<BoxSet>();
        var visited = new HashSet<Guid>();
        var items = new Dictionary<Guid, BaseItem>();
        pending.Enqueue(collection);
        while (pending.TryDequeue(out var current))
        {
            if (!visited.Add(current.Id))
            {
                continue;
            }

            var query = new InternalItemsQuery(user)
            {
                IsVirtualItem = false,
                GroupByPresentationUniqueKey = false,
                IncludeAlternateVersions = true,
                EnableTotalRecordCount = false,
                DtoOptions = new DtoOptions
                {
                    Fields = [ItemFields.ProviderIds],
                    EnableImages = false
                }
            };
            foreach (var child in current.GetChildren(user, true, query))
            {
                if (child.IsVirtualItem)
                {
                    continue;
                }

                switch (child)
                {
                    case Movie { ExtraType: null }:
                    case Episode:
                        items.TryAdd(child.Id, child);
                        break;
                    case Series or Season:
                        foreach (var episode in GetEpisodes(user, child))
                        {
                            items.TryAdd(episode.Id, episode);
                        }

                        break;
                    case BoxSet nested:
                        pending.Enqueue(nested);
                        break;
                }
            }
        }

        return items.Values.ToArray();
    }

    private static bool IsSupported(BaseItem item)
        => item is Movie { ExtraType: null } or Series or Season or Episode;

    private static string[] GetProviders(BaseItem item) => item is Movie ? _movieProviders : _seriesProviders;

    private static int? GetEpisodeEnd(Episode episode) => episode.IndexNumberEnd ?? episode.IndexNumber;

    private static string? GetProviderValue(BaseItem item, string provider)
    {
        foreach (var pair in item.ProviderIds)
        {
            if (string.Equals(pair.Key, provider, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(pair.Value))
            {
                return pair.Value.Trim();
            }
        }

        return null;
    }

    private static IReadOnlyList<BaseItem> IncludeSource(BaseItem source, IEnumerable<BaseItem> matches)
        => new[] { source }.Concat(matches).DistinctBy(item => item.Id).ToList();

    private static InternalItemsQuery CreateQuery(User user, BaseItemKind kind)
        => new(user)
        {
            IncludeItemTypes = [kind],
            Recursive = true,
            GroupByPresentationUniqueKey = false,
            IncludeAlternateVersions = true,
            EnableTotalRecordCount = false,
            DtoOptions = new DtoOptions
            {
                Fields = [ItemFields.ProviderIds],
                EnableImages = false
            }
        };

    private Series? GetSeries(BaseItem item)
        => item switch
        {
            Series series => series,
            Episode episode when !episode.SeriesId.Equals(Guid.Empty) => _libraryManager.GetItemById(episode.SeriesId) as Series,
            Season season when !season.SeriesId.Equals(Guid.Empty) => _libraryManager.GetItemById(season.SeriesId) as Series,
            _ => null
        };

    private bool IsInExpectedLibrary(User user, BaseItem item)
    {
        var expectedType = item is Movie ? CollectionType.movies : CollectionType.tvshows;
        return _libraryManager.GetCollectionFolders(item)
            .OfType<CollectionFolder>()
            .Any(folder => folder.CollectionType == expectedType && folder.IsVisible(user));
    }

    private IReadOnlyList<BaseItem> GetProviderMatches(User user, BaseItem source)
    {
        var knownProviderIds = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        AddProviderIds(source, knownProviderIds);
        var matches = new Dictionary<Guid, BaseItem> { [source.Id] = source };

        // A copy may bridge incomplete metadata (IMDb only -> IMDb + TMDB -> TMDB only).
        // Expand provider IDs until no further copy can be found, preserving their namespaces.
        var expanded = knownProviderIds.Count > 0;
        while (expanded)
        {
            expanded = false;
            var query = CreateQuery(user, source.GetBaseItemKind());
            query.HasAnyProviderIds = knownProviderIds.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
            query.IsVirtualItem = false;

            foreach (var candidate in _libraryManager.GetItemList(query))
            {
                if (candidate.GetBaseItemKind() != source.GetBaseItemKind()
                    || !IsSupported(candidate)
                    || matches.ContainsKey(candidate.Id)
                    || !IsInExpectedLibrary(user, candidate)
                    || !SharesProviderId(candidate, knownProviderIds))
                {
                    continue;
                }

                matches.Add(candidate.Id, candidate);
                expanded |= AddProviderIds(candidate, knownProviderIds);
            }
        }

        return matches.Values.ToList();
    }

    private static bool SharesProviderId(BaseItem item, Dictionary<string, HashSet<string>> providerIds)
        => providerIds.Any(pair => GetProviderValue(item, pair.Key) is { } value && pair.Value.Contains(value));

    private static bool AddProviderIds(BaseItem item, Dictionary<string, HashSet<string>> providerIds)
    {
        var changed = false;
        foreach (var provider in GetProviders(item))
        {
            var value = GetProviderValue(item, provider);
            if (value is null)
            {
                continue;
            }

            if (!providerIds.TryGetValue(provider, out var values))
            {
                values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                providerIds[provider] = values;
            }

            changed |= values.Add(value);
        }

        return changed;
    }
}
