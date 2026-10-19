using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Emby.Server.Implementations.Library;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Library;

public sealed class FavoriteLifecycleServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "jellyfin-favorites-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<Guid, BaseItem> _items = new();
    private readonly Dictionary<(Guid User, Guid Item), UserItemData> _data = new();
    private readonly Dictionary<Guid, IReadOnlyList<BaseItem>> _copies = new();
    private readonly Dictionary<Guid, IReadOnlyList<Episode>> _episodes = new();
    private readonly Mock<ILibraryManager> _library = new();
    private readonly Mock<IUserDataManager> _userData = new();
    private readonly Mock<IFavoriteItemMatcher> _matcher = new();
    private readonly Mock<IServerConfigurationManager> _configuration = new();
    private readonly ServerConfiguration _settings = new();
    private readonly User _user = new("user", "auth", "reset") { Id = Guid.NewGuid() };

    public FavoriteLifecycleServiceTests()
    {
        var paths = new Mock<IServerApplicationPaths>();
        paths.SetupGet(value => value.DataPath).Returns(_directory);
        _configuration.SetupGet(value => value.Configuration).Returns(_settings);
        _configuration.SetupGet(value => value.ApplicationPaths).Returns(paths.Object);
        _library.Setup(value => value.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery query) => _items.Values.Where(item => Data(query.User!, item).IsFavorite).ToArray());
        _library.Setup(value => value.GetItemById<BaseItem>(It.IsAny<Guid>(), It.IsAny<User>()))
            .Returns((Guid id, User user) => _items.GetValueOrDefault(id));
        _userData.Setup(value => value.GetUserData(It.IsAny<User>(), It.IsAny<BaseItem>()))
            .Returns((User user, BaseItem item) => Data(user, item));
        _userData.Setup(value => value.SaveUserData(It.IsAny<User>(), It.IsAny<BaseItem>(), It.IsAny<UpdateUserItemDataDto>(), It.IsAny<UserDataSaveReason>()))
            .Callback((User user, BaseItem item, UpdateUserItemDataDto update, UserDataSaveReason reason) =>
            {
                Assert.True(update.IsFavorite.HasValue);
                Assert.Null(update.Played);
                Assert.Null(update.PlaybackPositionTicks);
                Data(user, item).IsFavorite = update.IsFavorite.Value;
            });
        _matcher.Setup(value => value.GetEquivalentItems(It.IsAny<User>(), It.IsAny<BaseItem>()))
            .Returns((User user, BaseItem item) => _copies.GetValueOrDefault(item.Id) ?? new[] { item });
        _matcher.Setup(value => value.GetEpisodes(It.IsAny<User>(), It.IsAny<BaseItem>()))
            .Returns((User user, BaseItem item) => _episodes.GetValueOrDefault(item.Id) ?? Array.Empty<Episode>());
    }

    [Fact]
    public void DisabledKeys_OnlyChangesRequestedFavorite()
    {
        var copies = Movies(3);
        var service = Service();

        service.SetFavorite(_user, copies[0], true);
        service.RecordPlaybackCompletion(_user, copies[0], "play");

        Assert.True(Data(copies[0]).IsFavorite);
        Assert.False(Data(copies[1]).IsFavorite);
        Assert.False(Data(copies[2]).IsFavorite);
        _matcher.Verify(value => value.GetEquivalentItems(It.IsAny<User>(), It.IsAny<BaseItem>()), Times.Never);
    }

    [Fact]
    public void Sync_AddAndRemovePreservesEachCopiesPlaybackDataAndOtherUsers()
    {
        _settings.EnableFavoriteSyncAcrossLibraries = true;
        var copies = Movies(3);
        var otherUser = new User("other", "auth", "reset") { Id = Guid.NewGuid() };
        Data(copies[1]).Played = true;
        Data(copies[1]).PlaybackPositionTicks = 123;
        Data(otherUser, copies[1]).IsFavorite = true;
        var service = Service();

        service.SetFavorite(_user, copies[0], true);
        Assert.All(copies, copy => Assert.True(Data(copy).IsFavorite));
        service.SetFavorite(_user, copies[2], false);

        Assert.All(copies, copy => Assert.False(Data(copy).IsFavorite));
        Assert.True(Data(copies[1]).Played);
        Assert.Equal(123, Data(copies[1]).PlaybackPositionTicks);
        Assert.True(Data(otherUser, copies[1]).IsFavorite);
    }

    [Fact]
    public void ExistingFavorite_ExplicitFavoriteActionSynchronizesWithoutUsingHistoricalPlayed()
    {
        _settings.EnableFavoriteSyncAcrossLibraries = true;
        _settings.EnableAutoRemoveFavoritesAfterWatching = true;
        var copies = Movies(3);
        Data(copies[1]).IsFavorite = true;
        foreach (var copy in copies)
        {
            Data(copy).Played = true;
        }

        var service = Service();
        Assert.False(Data(copies[0]).IsFavorite);
        Assert.False(Data(copies[2]).IsFavorite);
        service.SetFavorite(_user, copies[1], true);
        Assert.All(copies, copy => Assert.True(Data(copy).IsFavorite));

        service.RecordPlaybackCompletion(_user, copies[2], "new-viewing");
        Assert.All(copies, copy => Assert.False(Data(copy).IsFavorite));
    }

    [Fact]
    public void AutoRemoval_AnyLibraryQualifiesIndependentlyOfSyncKey()
    {
        _settings.EnableAutoRemoveFavoritesAfterWatching = true;
        var copies = Movies(3);
        Data(copies[0]).Played = true;
        var service = Service();
        service.SetFavorite(_user, copies[0], true);
        Assert.True(Data(copies[0]).IsFavorite);

        service.RecordPlaybackCompletion(_user, copies[2], "new-play");

        Assert.All(copies, copy => Assert.False(Data(copy).IsFavorite));
    }

    [Fact]
    public void Playback_DuplicateCompletionAndStopCannotRemoveRefavorite_EvenAfterRestart()
    {
        _settings.EnableAutoRemoveFavoritesAfterWatching = true;
        var copies = Movies(2);
        var service = Service();
        service.SetFavorite(_user, copies[0], true);
        service.RecordPlaybackCompletion(_user, copies[1], "first-play");
        Assert.False(Data(copies[0]).IsFavorite);
        service.SetFavorite(_user, copies[0], true);

        service = Service();
        service.RecordPlaybackCompletion(_user, copies[1], "first-play");
        Assert.True(Data(copies[0]).IsFavorite);

        service.RecordPlaybackCompletion(_user, copies[1], "second-play");
        Assert.False(Data(copies[0]).IsFavorite);
    }

    [Fact]
    public void Playback_CompletionBeforeFavoritingDoesNotBecomeNewViewingOnNextReport()
    {
        _settings.EnableAutoRemoveFavoritesAfterWatching = true;
        var copies = Movies(1);
        var service = Service();
        service.RecordPlaybackCompletion(_user, copies[0], "finished-before-favorite");
        service.SetFavorite(_user, copies[0], true);
        service = Service();

        service.RecordPlaybackCompletion(_user, copies[0], "finished-before-favorite");

        Assert.True(Data(copies[0]).IsFavorite);
    }

    [Fact]
    public void ManualPlayed_AlreadyPlayedItemCountsWithoutResettingPlayed()
    {
        _settings.EnableFavoriteSyncAcrossLibraries = true;
        _settings.EnableAutoRemoveFavoritesAfterWatching = true;
        var copies = Movies(3);
        Data(copies[0]).Played = true;
        var service = Service();
        service.SetFavorite(_user, copies[0], true);

        service.RecordMarkedPlayed(_user, copies[2]);

        Assert.All(copies, copy => Assert.False(Data(copy).IsFavorite));
        Assert.True(Data(copies[0]).Played);
    }

    [Fact]
    public void Series_NewFullCycleSurvivesRestartAndDuplicateLibraryEpisodes()
    {
        _settings.EnableFavoriteSyncAcrossLibraries = true;
        _settings.EnableAutoRemoveFavoritesAfterWatching = true;
        var (series, episodes) = Television();
        foreach (var episode in episodes)
        {
            Data(episode).Played = true;
        }

        var service = Service();
        service.SetFavorite(_user, series[0], true);
        service.RecordPlaybackCompletion(_user, episodes[2], "first-episode");
        Assert.All(series, copy => Assert.True(Data(copy).IsFavorite));

        service = Service();
        service.SetFavorite(_user, series[1], true);
        service.RecordMarkedPlayed(_user, episodes[0]);
        Assert.All(series, copy => Assert.True(Data(copy).IsFavorite));
        service.RecordMarkedPlayed(_user, episodes[3]);

        Assert.All(series, copy => Assert.False(Data(copy).IsFavorite));
        Assert.All(episodes, episode => Assert.False(Data(episode).IsFavorite));
    }

    [Fact]
    public void Season_CompletesOnlyItsEpisodesWhileSeriesStillWaitsForOtherSeasons()
    {
        _settings.EnableFavoriteSyncAcrossLibraries = true;
        _settings.EnableAutoRemoveFavoritesAfterWatching = true;
        var (series, episodes) = Television();
        var seasons = Group(new Season { SeriesId = series[0].Id, IndexNumber = 1 }, new Season { SeriesId = series[1].Id, IndexNumber = 1 });
        foreach (var season in seasons)
        {
            _episodes[season.Id] = episodes;
        }

        var extra = Add(new Episode { SeriesId = series[0].Id, ParentIndexNumber = 2, IndexNumber = 1 });
        foreach (var copy in series)
        {
            _episodes[copy.Id] = episodes.Append(extra).ToArray();
        }

        var service = Service();
        service.SetFavorite(_user, series[0], true);
        service.SetFavorite(_user, seasons[0], true);
        service.RecordMarkedPlayed(_user, episodes[0]);
        service.RecordMarkedPlayed(_user, episodes[1]);

        Assert.All(seasons, copy => Assert.False(Data(copy).IsFavorite));
        Assert.All(series, copy => Assert.True(Data(copy).IsFavorite));
        service.RecordMarkedPlayed(_user, extra);
        Assert.All(series, copy => Assert.False(Data(copy).IsFavorite));
    }

    [Fact]
    public void ManualWholeSeries_CompletesFavoriteSeasonsAndEpisodesToo()
    {
        _settings.EnableFavoriteSyncAcrossLibraries = true;
        _settings.EnableAutoRemoveFavoritesAfterWatching = true;
        var (series, episodes) = Television();
        var seasons = Group(new Season { SeriesId = series[0].Id, IndexNumber = 1 }, new Season { SeriesId = series[1].Id, IndexNumber = 1 });
        foreach (var season in seasons)
        {
            _episodes[season.Id] = episodes;
        }

        var service = Service();
        service.SetFavorite(_user, series[0], true);
        service.SetFavorite(_user, seasons[0], true);
        service.SetFavorite(_user, episodes[0], true);

        service.RecordMarkedPlayed(_user, series[1]);

        Assert.All(series.Cast<BaseItem>().Concat(seasons).Concat(episodes), copy => Assert.False(Data(copy).IsFavorite));
    }

    [Fact]
    public void CompoundEpisode_CompletesBothLogicalEpisodesInContainer()
    {
        _settings.EnableAutoRemoveFavoritesAfterWatching = true;
        var (series, episodes) = Television();
        var compound = Add(new Episode { SeriesId = series[1].Id, ParentIndexNumber = 1, IndexNumber = 1, IndexNumberEnd = 2 });
        var service = Service();
        service.SetFavorite(_user, series[0], true);

        service.RecordPlaybackCompletion(_user, compound, "double-episode");

        Assert.False(Data(series[0]).IsFavorite);
    }

    [Fact]
    public void RemovingAndRefavoritingContainer_StartsANewFullCycle()
    {
        _settings.EnableAutoRemoveFavoritesAfterWatching = true;
        var (series, episodes) = Television();
        var service = Service();
        service.SetFavorite(_user, series[0], true);
        service.RecordMarkedPlayed(_user, episodes[0]);
        service.SetFavorite(_user, series[0], false);
        service.SetFavorite(_user, series[0], true);
        service.RecordMarkedPlayed(_user, episodes[1]);
        Assert.True(Data(series[0]).IsFavorite);
        service.RecordMarkedPlayed(_user, episodes[0]);
        Assert.False(Data(series[0]).IsFavorite);
    }

    [Fact]
    public void Series_NewlyImportedEpisodeMustBeCompletedBeforeRemoval()
    {
        _settings.EnableAutoRemoveFavoritesAfterWatching = true;
        var (series, episodes) = Television();
        var service = Service();
        service.SetFavorite(_user, series[0], true);
        service.RecordMarkedPlayed(_user, episodes[0]);
        var imported = Add(new Episode { SeriesId = series[1].Id, ParentIndexNumber = 1, IndexNumber = 3 });
        foreach (var copy in series)
        {
            _episodes[copy.Id] = episodes.Append(imported).ToArray();
        }

        service.RecordMarkedPlayed(_user, episodes[1]);
        Assert.True(Data(series[0]).IsFavorite);
        service.RecordMarkedPlayed(_user, imported);
        Assert.False(Data(series[0]).IsFavorite);
    }

    [Fact]
    public void Series_DeletedEpisodeDoesNotBlockCompletionOfRemainingEpisodes()
    {
        _settings.EnableAutoRemoveFavoritesAfterWatching = true;
        var (series, episodes) = Television();
        var service = Service();
        service.SetFavorite(_user, series[0], true);
        foreach (var copy in series)
        {
            _episodes[copy.Id] = new[] { episodes[0], episodes[2] };
        }

        service.RecordMarkedPlayed(_user, episodes[2]);

        Assert.False(Data(series[0]).IsFavorite);
    }

    [Fact]
    public void ExistingFavorite_TrackingInitializesOnNewViewingWithoutCopyingFavorites()
    {
        _settings.EnableAutoRemoveFavoritesAfterWatching = true;
        var (series, episodes) = Television();
        Data(series[0]).IsFavorite = true;
        var service = Service();

        service.RecordMarkedPlayed(_user, episodes[0]);
        Assert.True(Data(series[0]).IsFavorite);
        Assert.False(Data(series[1]).IsFavorite);
        service.RecordMarkedPlayed(_user, episodes[3]);
        Assert.False(Data(series[0]).IsFavorite);
    }

    [Fact]
    public void Playback_UserIsolationIncludesPersistedSessionDeduplication()
    {
        _settings.EnableAutoRemoveFavoritesAfterWatching = true;
        var copies = Movies(2);
        var other = new User("other", "auth", "reset") { Id = Guid.NewGuid() };
        var service = Service();
        service.SetFavorite(_user, copies[0], true);
        service.SetFavorite(other, copies[0], true);

        service.RecordPlaybackCompletion(_user, copies[1], "shared-session-id");
        Assert.False(Data(copies[0]).IsFavorite);
        Assert.True(Data(other, copies[0]).IsFavorite);
        service.RecordPlaybackCompletion(other, copies[1], "shared-session-id");
        Assert.False(Data(other, copies[0]).IsFavorite);
    }

    [Fact]
    public void InterruptedRemoval_ResumesAllCompletedCyclesBeforeSkippingDuplicateSession()
    {
        _settings.EnableFavoriteSyncAcrossLibraries = true;
        _settings.EnableAutoRemoveFavoritesAfterWatching = true;
        var (series, episodes) = Television();
        var seasons = Group(new Season { SeriesId = series[0].Id, IndexNumber = 1 }, new Season { SeriesId = series[1].Id, IndexNumber = 1 });
        foreach (var season in seasons)
        {
            _episodes[season.Id] = episodes;
        }

        var service = Service();
        service.SetFavorite(_user, series[0], true);
        service.SetFavorite(_user, seasons[0], true);
        service.SetFavorite(_user, episodes[0], true);
        service.RecordMarkedPlayed(_user, episodes[1]);
        var failOnce = true;
        _userData.Setup(value => value.SaveUserData(It.IsAny<User>(), It.IsAny<BaseItem>(), It.IsAny<UpdateUserItemDataDto>(), It.IsAny<UserDataSaveReason>()))
            .Callback((User user, BaseItem item, UpdateUserItemDataDto update, UserDataSaveReason reason) =>
            {
                if (update.IsFavorite == false && failOnce)
                {
                    failOnce = false;
                    throw new IOException("Simulated interruption between persisted completion and userdata removal");
                }

                Data(user, item).IsFavorite = update.IsFavorite!.Value;
            });

        Assert.Throws<IOException>(() => service.RecordPlaybackCompletion(_user, episodes[0], "last-episode"));
        service = Service();
        service.RecordPlaybackCompletion(_user, episodes[0], "last-episode");

        Assert.All(series.Cast<BaseItem>().Concat(seasons).Concat(episodes), copy => Assert.False(Data(copy).IsFavorite));
    }

    [Fact]
    public void ComputationFailure_DoesNotConsumePlaybackCompletionBeforeItWasApplied()
    {
        _settings.EnableAutoRemoveFavoritesAfterWatching = true;
        var (series, episodes) = Television();
        var service = Service();
        service.SetFavorite(_user, series[0], true);
        var failOnce = true;
        _matcher.Setup(value => value.GetEpisodes(It.IsAny<User>(), It.IsAny<BaseItem>()))
            .Returns((User user, BaseItem item) =>
            {
                if (failOnce)
                {
                    failOnce = false;
                    throw new IOException("Simulated episode query failure before committing completion");
                }

                return _episodes.GetValueOrDefault(item.Id) ?? Array.Empty<Episode>();
            });

        Assert.Throws<IOException>(() => service.RecordPlaybackCompletion(_user, episodes[0], "retry-after-query-failure"));
        service.RecordPlaybackCompletion(_user, episodes[0], "retry-after-query-failure");
        service.RecordMarkedPlayed(_user, episodes[1]);

        Assert.False(Data(series[0]).IsFavorite);
    }

    [Fact]
    public void CompoundPlayback_RemovesExistingAndNewSingleEpisodeFavoritesAcrossLibraries()
    {
        _settings.EnableAutoRemoveFavoritesAfterWatching = true;
        var (series, episodes) = Television();
        var compound = Add(new Episode { SeriesId = series[1].Id, ParentIndexNumber = 1, IndexNumber = 1, IndexNumberEnd = 2 });
        Data(episodes[0]).IsFavorite = true;
        var service = Service();
        service.SetFavorite(_user, episodes[1], true);

        service.RecordPlaybackCompletion(_user, compound, "compound-completion");

        Assert.False(Data(episodes[0]).IsFavorite);
        Assert.False(Data(episodes[1]).IsFavorite);
        Assert.All(episodes, episode => Assert.False(Data(episode).IsFavorite));
        Assert.False(Data(compound).IsFavorite);
    }

    [Fact]
    public void CompoundFavorite_RequiresBothSeparateEpisodesAcrossLibrariesAndRestart()
    {
        _settings.EnableAutoRemoveFavoritesAfterWatching = true;
        var (series, episodes) = Television();
        var compound = Add(new Episode { SeriesId = series[0].Id, ParentIndexNumber = 1, IndexNumber = 1, IndexNumberEnd = 2 });
        var service = Service();
        service.SetFavorite(_user, compound, true);

        service.RecordPlaybackCompletion(_user, episodes[2], "first-single-completion");
        Assert.True(Data(compound).IsFavorite);
        service = Service();
        service.RecordPlaybackCompletion(_user, episodes[3], "second-single-completion");

        Assert.False(Data(compound).IsFavorite);
        Assert.All(episodes, episode => Assert.False(Data(episode).IsFavorite));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
    }

    private FavoriteLifecycleService Service() => new(_configuration.Object, _library.Object, _userData.Object, _matcher.Object, NullLogger<FavoriteLifecycleService>.Instance);

    private UserItemData Data(BaseItem item) => Data(_user, item);

    private UserItemData Data(User user, BaseItem item)
    {
        var key = (user.Id, item.Id);
        if (!_data.TryGetValue(key, out var data))
        {
            data = new UserItemData { Key = item.Id.ToString("N") };
            _data.Add(key, data);
        }

        return data;
    }

    private T Add<T>(T item)
        where T : BaseItem
    {
        item.Id = Guid.NewGuid();
        _items.Add(item.Id, item);
        return item;
    }

    private T[] Group<T>(params T[] items)
        where T : BaseItem
    {
        foreach (var item in items)
        {
            Add(item);
            _copies[item.Id] = items;
        }

        return items;
    }

    private Movie[] Movies(int count) => Group(Enumerable.Range(0, count).Select(_ => new Movie()).ToArray());

    private (Series[] Series, Episode[] Episodes) Television()
    {
        var series = Group(new Series(), new Series());
        var first = Group(new Episode { SeriesId = series[0].Id, ParentIndexNumber = 1, IndexNumber = 1 }, new Episode { SeriesId = series[1].Id, ParentIndexNumber = 1, IndexNumber = 1 });
        var second = Group(new Episode { SeriesId = series[0].Id, ParentIndexNumber = 1, IndexNumber = 2 }, new Episode { SeriesId = series[1].Id, ParentIndexNumber = 1, IndexNumber = 2 });
        var episodes = new[] { first[0], second[0], first[1], second[1] };
        foreach (var copy in series)
        {
            _episodes[copy.Id] = episodes;
        }

        return (series, episodes);
    }
}
