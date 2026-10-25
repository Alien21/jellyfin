using System;
using System.Collections.Generic;
using System.Linq;
using Emby.Server.Implementations.Library;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Library;

public sealed class FavoriteItemMatcherTests
{
    private readonly Mock<ILibraryManager> _library = new();
    private readonly Dictionary<Guid, BaseItem> _items = new();
    private readonly Dictionary<Guid, CollectionFolder> _folders = new();
    private readonly User _user = new("favorites", "auth", "reset");
    private readonly FavoriteItemMatcher _matcher;

    public FavoriteItemMatcherTests()
    {
        _library.Setup(library => library.GetItemById(It.IsAny<Guid>()))
            .Returns<Guid>(id => _items.GetValueOrDefault(id));
        _library.Setup(library => library.GetCollectionFolders(It.IsAny<BaseItem>()))
            .Returns<BaseItem>(item => _folders.TryGetValue(item.Id, out var folder) ? [folder] : []);
        _library.Setup(library => library.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(QueryItems);
        _matcher = new FavoriteItemMatcher(_library.Object);
    }

    [Fact]
    public void Movies_PartialProviderCoverageFindsEveryCopy()
    {
        var original = Add(new Movie { ProviderIds = Ids(("Imdb", "tt001")) });
        var bridge = Add(new Movie { ProviderIds = Ids(("Imdb", "tt001"), ("Tmdb", "17")) });
        var tmdbOnly = Add(new Movie { ProviderIds = Ids(("Tmdb", "17")) });

        Assert.Equal(new[] { original.Id, bridge.Id, tmdbOnly.Id }.Order(), _matcher.GetEquivalentItems(_user, original).Select(item => item.Id).Order());
        _library.Verify(
            library => library.GetItemList(It.Is<InternalItemsQuery>(query =>
                query.User == _user && !query.GroupByPresentationUniqueKey && query.IncludeAlternateVersions
                && query.DtoOptions.ContainsField(ItemFields.ProviderIds))),
            Times.AtLeastOnce());
    }

    [Fact]
    public void Movies_OnlySameTypeVisibleMovieLibrariesAndProviderNamespaceMatch()
    {
        var original = Add(new Movie { ProviderIds = Ids(("Tmdb", "17")) });
        var copy = Add(new Movie { ProviderIds = Ids(("Tmdb", "17")) });
        Add(new Movie { ProviderIds = Ids(("Tvdb", "17")) });
        Add(new Series { ProviderIds = Ids(("Tmdb", "17")) });
        Add(new Movie { ProviderIds = Ids(("Tmdb", "17")) }, CollectionType.tvshows);
        Add(new Movie { ProviderIds = Ids(("Tmdb", "17")) }, visible: false);

        Assert.Equal(new[] { original.Id, copy.Id }.Order(), _matcher.GetEquivalentItems(_user, original).Select(item => item.Id).Order());
    }

    [Fact]
    public void Movies_UnidentifiedSameNameAndYearRemainSeparate()
    {
        var original = Add(new Movie { Name = "A film", ProductionYear = 2020 });
        Add(new Movie { Name = "A film", ProductionYear = 2020 });

        Assert.Same(original, Assert.Single(_matcher.GetEquivalentItems(_user, original)));
        _library.Verify(library => library.GetItemList(It.IsAny<InternalItemsQuery>()), Times.Never());
    }

    [Fact]
    public void Series_SharedMetadataFindsCopiesWithoutIncludingSeasons()
    {
        var original = Add(new Series { ProviderIds = Ids(("Tvdb", "42")) });
        var copy = Add(new Series { ProviderIds = Ids(("Tvdb", "42")) });
        Add(new Season { SeriesId = copy.Id, IndexNumber = 1, ProviderIds = Ids(("Tvdb", "42")) });

        Assert.Equal(new[] { original.Id, copy.Id }.Order(), _matcher.GetEquivalentItems(_user, original).Select(item => item.Id).Order());
    }

    [Fact]
    public void Seasons_SpecialsMatchSameSeriesAndNumberOnly()
    {
        var originalSeries = Add(new Series { ProviderIds = Ids(("Tvdb", "42")) });
        var copySeries = Add(new Series { ProviderIds = Ids(("Tvdb", "42")) });
        var otherSeries = Add(new Series { ProviderIds = Ids(("Tvdb", "99")) });
        var original = Add(new Season { SeriesId = originalSeries.Id, IndexNumber = 0 });
        var copy = Add(new Season { SeriesId = copySeries.Id, IndexNumber = 0 });
        Add(new Season { SeriesId = copySeries.Id, IndexNumber = 1 });
        Add(new Season { SeriesId = otherSeries.Id, IndexNumber = 0 });

        Assert.Equal(new[] { original.Id, copy.Id }.Order(), _matcher.GetEquivalentItems(_user, original).Select(item => item.Id).Order());
        Assert.Equal(_matcher.GetIdentityKey(original), _matcher.GetIdentityKey(copy));
    }

    [Fact]
    public void Seasons_MissingNumberDoesNotMatchSpecials()
    {
        var series = Add(new Series { ProviderIds = Ids(("Tvdb", "42")) });
        var original = Add(new Season { SeriesId = series.Id });
        Add(new Season { SeriesId = series.Id, IndexNumber = 0 });

        Assert.Same(original, Assert.Single(_matcher.GetEquivalentItems(_user, original)));
    }

    [Fact]
    public void Episodes_MatchSeriesSeasonAndExactCombinedRange()
    {
        var originalSeries = Add(new Series { ProviderIds = Ids(("Imdb", "tt042")) });
        var copySeries = Add(new Series { ProviderIds = Ids(("Imdb", "tt042")) });
        var otherSeries = Add(new Series { ProviderIds = Ids(("Imdb", "tt099")) });
        var original = Add(new Episode { SeriesId = originalSeries.Id, ParentIndexNumber = 1, IndexNumber = 2, IndexNumberEnd = 3 });
        var copy = Add(new Episode { SeriesId = copySeries.Id, ParentIndexNumber = 1, IndexNumber = 2, IndexNumberEnd = 3 });
        Add(new Episode { SeriesId = copySeries.Id, ParentIndexNumber = 1, IndexNumber = 2 });
        Add(new Episode { SeriesId = copySeries.Id, ParentIndexNumber = 0, IndexNumber = 2, IndexNumberEnd = 3 });
        Add(new Episode { SeriesId = otherSeries.Id, ParentIndexNumber = 1, IndexNumber = 2, IndexNumberEnd = 3 });

        Assert.Equal(new[] { original.Id, copy.Id }.Order(), _matcher.GetEquivalentItems(_user, original).Select(item => item.Id).Order());
        Assert.Equal(_matcher.GetIdentityKey(original), _matcher.GetIdentityKey(copy));
    }

    [Fact]
    public void Episodes_NumberOneWithoutRangeMatchesExplicitOneEpisodeRange()
    {
        var series = Add(new Series { ProviderIds = Ids(("Tvdb", "42")) });
        var original = Add(new Episode { SeriesId = series.Id, ParentIndexNumber = 0, IndexNumber = 1 });
        var copy = Add(new Episode { SeriesId = series.Id, ParentIndexNumber = 0, IndexNumber = 1, IndexNumberEnd = 1 });

        Assert.Equal(2, _matcher.GetEquivalentItems(_user, original).Count);
        Assert.Equal(_matcher.GetIdentityKey(original), _matcher.GetIdentityKey(copy));
    }

    [Fact]
    public void GetEpisodes_DeduplicatesLibraryCopiesWithDifferentProviderCoverage()
    {
        var originalSeries = Add(new Series { ProviderIds = Ids(("Imdb", "tt042")) });
        var copySeries = Add(new Series { ProviderIds = Ids(("Imdb", "tt042"), ("Tvdb", "42")) });
        var otherSeries = Add(new Series { ProviderIds = Ids(("Tvdb", "99")) });
        Add(new Episode { SeriesId = originalSeries.Id, ParentIndexNumber = 1, IndexNumber = 1 });
        Add(new Episode { SeriesId = copySeries.Id, ParentIndexNumber = 1, IndexNumber = 1 });
        Add(new Episode { SeriesId = copySeries.Id, ParentIndexNumber = 1, IndexNumber = 2 });
        Add(new Episode { SeriesId = originalSeries.Id, ParentIndexNumber = 0, IndexNumber = 1 });
        Add(new Episode { SeriesId = copySeries.Id, ParentIndexNumber = 1, IndexNumber = 3, IsVirtualItem = true });
        Add(new Episode { SeriesId = otherSeries.Id, ParentIndexNumber = 2, IndexNumber = 9 });

        var episodes = _matcher.GetEpisodes(_user, originalSeries);

        Assert.Equal(3, episodes.Count);
        Assert.Contains(episodes, episode => episode.ParentIndexNumber == 0);
        Assert.DoesNotContain(episodes, episode => episode.IndexNumber == 3 || episode.SeriesId.Equals(otherSeries.Id));
    }

    [Fact]
    public void GetEpisodes_SeasonDoesNotRequireOtherSeasons()
    {
        var series = Add(new Series { ProviderIds = Ids(("Tvdb", "42")) });
        var season = Add(new Season { SeriesId = series.Id, IndexNumber = 2 });
        Add(new Episode { SeriesId = series.Id, SeasonId = season.Id, ParentIndexNumber = 2, IndexNumber = 1 });
        Add(new Episode { SeriesId = series.Id, ParentIndexNumber = 1, IndexNumber = 1 });

        Assert.Equal(2, Assert.Single(_matcher.GetEpisodes(_user, season)).ParentIndexNumber);
    }

    [Fact]
    public void GetCollectionItems_UsesUserScopeWithoutFilteringPlayedAndExcludesExtras()
    {
        var movie = Add(new Movie());
        var episode = Add(new Episode());
        var extra = Add(new Movie { ExtraType = ExtraType.Trailer });
        var virtualMovie = Add(new Movie { IsVirtualItem = true });
        var virtualEpisode = Add(new Episode { IsVirtualItem = true });
        var collection = Collection(movie, episode, extra, virtualMovie, virtualEpisode, new Folder());

        var items = _matcher.GetCollectionItems(_user, collection.Object);

        Assert.Equal(new[] { movie.Id, episode.Id }.Order(), items.Select(item => item.Id).Order());
        collection.Verify(
            value => value.GetChildren(
                _user,
                true,
                It.Is<InternalItemsQuery>(query =>
                    query.User == _user && query.IsPlayed == null && query.IsVirtualItem == false
                    && !query.GroupByPresentationUniqueKey && query.IncludeAlternateVersions
                    && query.DtoOptions.ContainsField(ItemFields.ProviderIds))),
            Times.Once);
    }

    [Fact]
    public void GetCollectionItems_NestedCycleTerminatesAndKeepsDistinctLibraryCopies()
    {
        var first = Add(new Movie { ProviderIds = Ids(("Tmdb", "17")) });
        var copy = Add(new Movie { ProviderIds = Ids(("Tmdb", "17")) });
        var collection = Collection();
        var nested = Collection(first, copy, collection.Object);
        collection.Setup(value => value.GetChildren(_user, true, It.IsAny<InternalItemsQuery>()))
            .Returns(new BaseItem[] { first, nested.Object, nested.Object });

        var items = _matcher.GetCollectionItems(_user, collection.Object);

        Assert.Equal(new[] { first.Id, copy.Id }.Order(), items.Select(item => item.Id).Order());
        collection.Verify(value => value.GetChildren(_user, true, It.IsAny<InternalItemsQuery>()), Times.Once);
        nested.Verify(value => value.GetChildren(_user, true, It.IsAny<InternalItemsQuery>()), Times.Once);
    }

    [Fact]
    public void GetCollectionItems_SeriesAndSeasonExpandToAvailableEpisodesWithoutDuplicates()
    {
        var series = Add(new Series());
        var season = Add(new Season { SeriesId = series.Id, IndexNumber = 2 });
        var first = Add(new Episode { SeriesId = series.Id, ParentIndexNumber = 1, IndexNumber = 1 });
        var second = Add(new Episode { SeriesId = series.Id, SeasonId = season.Id, ParentIndexNumber = 2, IndexNumber = 1 });
        Add(new Episode { SeriesId = series.Id, ParentIndexNumber = 2, IndexNumber = 2, IsVirtualItem = true });
        var otherSeries = Add(new Series());
        Add(new Episode { SeriesId = otherSeries.Id, ParentIndexNumber = 1, IndexNumber = 1 });
        var collection = Collection(series, season);

        var items = _matcher.GetCollectionItems(_user, collection.Object);

        Assert.Equal(new[] { first.Id, second.Id }.Order(), items.Select(item => item.Id).Order());
    }

    [Fact]
    public void Collections_SharedProviderIdDoesNotSynchronizeSeparateCollections()
    {
        var collection = Add(new BoxSet { ProviderIds = Ids(("Tmdb", "17")) });
        Add(new BoxSet { ProviderIds = Ids(("Tmdb", "17")) });

        Assert.Same(collection, Assert.Single(_matcher.GetEquivalentItems(_user, collection)));
        _library.Verify(value => value.GetItemList(It.IsAny<InternalItemsQuery>()), Times.Never());
    }

    private Mock<BoxSet> Collection(params BaseItem[] children)
    {
        var collection = new Mock<BoxSet>();
        collection.Object.Id = Guid.NewGuid();
        collection.Setup(value => value.GetChildren(_user, true, It.IsAny<InternalItemsQuery>())).Returns(children);
        return collection;
    }

    private static Dictionary<string, string> Ids(params (string Provider, string Id)[] values)
        => values.ToDictionary(value => value.Provider, value => value.Id, StringComparer.OrdinalIgnoreCase);

    private T Add<T>(T item, CollectionType? libraryType = null, bool visible = true)
        where T : BaseItem
    {
        item.Id = Guid.NewGuid();
        _items[item.Id] = item;
        var folder = new Mock<CollectionFolder>();
        folder.Object.CollectionType = libraryType ?? (item is Movie ? CollectionType.movies : CollectionType.tvshows);
        folder.Setup(library => library.IsVisible(It.IsAny<User>(), It.IsAny<bool>())).Returns(visible);
        _folders[item.Id] = folder.Object;
        return item;
    }

    private IReadOnlyList<BaseItem> QueryItems(InternalItemsQuery query)
    {
        return _items.Values.Where(item => query.IncludeItemTypes.Contains(item.GetBaseItemKind()))
            .Where(item => !query.IsVirtualItem.HasValue || item.IsVirtualItem == query.IsVirtualItem)
            .Where(item => query.HasAnyProviderIds is null || query.HasAnyProviderIds.Any(provider =>
                item.ProviderIds.TryGetValue(provider.Key, out var value) && provider.Value.Contains(value, StringComparer.OrdinalIgnoreCase)))
            .Where(item => !query.IndexNumber.HasValue || item.IndexNumber == query.IndexNumber)
            .Where(item => !query.ParentIndexNumber.HasValue || item.ParentIndexNumber == query.ParentIndexNumber)
            .Where(item => query.AncestorIds.Length == 0 || item switch
            {
                Season season => query.AncestorIds.Contains(season.SeriesId),
                Episode episode => query.AncestorIds.Contains(episode.SeriesId) || query.AncestorIds.Contains(episode.SeasonId),
                _ => false
            })
            .ToList();
    }
}
