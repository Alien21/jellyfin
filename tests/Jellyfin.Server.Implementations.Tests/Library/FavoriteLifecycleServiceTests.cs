using System;
using System.Collections.Generic;
using System.Linq;
using Emby.Server.Implementations.Library;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Library;

public sealed class FavoriteLifecycleServiceTests
{
    private readonly Dictionary<(Guid User, Guid Item), UserItemData> _data = new();
    private readonly Dictionary<Guid, IReadOnlyList<BaseItem>> _copies = new();
    private readonly Mock<IUserDataManager> _userData = new();
    private readonly Mock<IFavoriteItemMatcher> _matcher = new();
    private readonly Mock<IServerConfigurationManager> _configuration = new();
    private readonly ServerConfiguration _settings = new();
    private readonly User _user = new("user", "auth", "reset") { Id = Guid.NewGuid() };

    public FavoriteLifecycleServiceTests()
    {
        _configuration.SetupGet(value => value.Configuration).Returns(_settings);
        _userData.Setup(value => value.GetUserData(It.IsAny<User>(), It.IsAny<BaseItem>()))
            .Returns((User user, BaseItem item) => Data(user, item));
        _userData.Setup(value => value.SaveUserData(It.IsAny<User>(), It.IsAny<BaseItem>(), It.IsAny<UpdateUserItemDataDto>(), It.IsAny<UserDataSaveReason>()))
            .Callback((User user, BaseItem item, UpdateUserItemDataDto update, UserDataSaveReason reason) =>
            {
                Assert.True(update.IsFavorite.HasValue);
                Assert.Null(update.Played);
                Assert.Null(update.PlaybackPositionTicks);
                Assert.Null(update.PlayCount);
                Assert.Null(update.LastPlayedDate);
                Assert.Null(update.Rating);
                Data(user, item).IsFavorite = update.IsFavorite.Value;
            });
        _matcher.Setup(value => value.GetEquivalentItems(It.IsAny<User>(), It.IsAny<BaseItem>()))
            .Returns((User user, BaseItem item) => _copies.GetValueOrDefault(item.Id) ?? new[] { item });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DisabledSync_OnlyChangesRequestedFavorite(bool isFavorite)
    {
        var copies = Copies("Movie");
        foreach (var copy in copies)
        {
            Data(_user, copy).IsFavorite = !isFavorite;
        }

        Service().SetFavorite(_user, copies[0], isFavorite);

        Assert.Equal(isFavorite, Data(_user, copies[0]).IsFavorite);
        Assert.Equal(!isFavorite, Data(_user, copies[1]).IsFavorite);
        Assert.Equal(!isFavorite, Data(_user, copies[2]).IsFavorite);
        _matcher.Verify(value => value.GetEquivalentItems(It.IsAny<User>(), It.IsAny<BaseItem>()), Times.Never);
    }

    [Theory]
    [InlineData("Movie")]
    [InlineData("Episode")]
    [InlineData("Season")]
    [InlineData("Series")]
    public void Sync_AddsAndRemovesEveryMatchingCopy(string kind)
    {
        _settings.EnableFavoriteSyncAcrossLibraries = true;
        var copies = Copies(kind);
        var service = Service();

        service.SetFavorite(_user, copies[0], true);
        Assert.All(copies, copy => Assert.True(Data(_user, copy).IsFavorite));
        service.SetFavorite(_user, copies[2], false);

        Assert.All(copies, copy => Assert.False(Data(_user, copy).IsFavorite));
    }

    [Fact]
    public void Sync_PreservesPlaybackDataAndOtherUsers()
    {
        _settings.EnableFavoriteSyncAcrossLibraries = true;
        var copies = Copies("Movie");
        var otherUser = new User("other", "auth", "reset") { Id = Guid.NewGuid() };
        var watchedCopy = Data(_user, copies[1]);
        var lastPlayed = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        watchedCopy.Played = true;
        watchedCopy.PlaybackPositionTicks = 123;
        watchedCopy.PlayCount = 4;
        watchedCopy.LastPlayedDate = lastPlayed;
        watchedCopy.Rating = 9;
        Data(otherUser, copies[1]).IsFavorite = true;
        var service = Service();

        service.SetFavorite(_user, copies[0], true);
        Assert.All(copies, copy => Assert.True(Data(_user, copy).IsFavorite));
        service.SetFavorite(_user, copies[2], false);

        Assert.All(copies, copy => Assert.False(Data(_user, copy).IsFavorite));
        Assert.True(watchedCopy.Played);
        Assert.Equal(123, watchedCopy.PlaybackPositionTicks);
        Assert.Equal(4, watchedCopy.PlayCount);
        Assert.Equal(lastPlayed, watchedCopy.LastPlayedDate);
        Assert.Equal(9, watchedCopy.Rating);
        Assert.True(Data(otherUser, copies[1]).IsFavorite);
    }

    private FavoriteLifecycleService Service() => new(_configuration.Object, _userData.Object, _matcher.Object);

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

    private BaseItem[] Copies(string kind)
    {
        var copies = Enumerable.Range(0, 3).Select(_ => kind switch
        {
            "Movie" => (BaseItem)new Movie(),
            "Episode" => new Episode(),
            "Season" => new Season(),
            "Series" => new Series(),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        }).ToArray();
        foreach (var copy in copies)
        {
            copy.Id = Guid.NewGuid();
            _copies[copy.Id] = copies;
        }

        return copies;
    }
}
