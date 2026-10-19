using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Devices;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.SessionManager;

public class FavoritePlaybackHooksTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task Progress_RecordsQualifiedCompletionInsteadOfHistoricalPlayedStatus(bool completed, bool wasPlayed, bool automated)
    {
        await using var fixture = new PlaybackFixture();
        fixture.Data.Played = wasPlayed;
        fixture.UserData.Setup(i => i.UpdatePlayState(fixture.Item, fixture.Data, 95L)).Returns(completed);
        var session = await fixture.OpenSession();

        await fixture.Manager.OnPlaybackProgress(fixture.Progress(session, "playback-1"), automated);

        fixture.Favorites.Verify(
            i => i.RecordPlaybackCompletion(fixture.User, fixture.Item, "playback-1"),
            completed && !automated ? Times.Once() : Times.Never());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Stop_RecordsOnlySuccessfulQualifiedCompletion(bool completed, bool failed)
    {
        await using var fixture = new PlaybackFixture();
        fixture.Data.Played = true;
        fixture.UserData.Setup(i => i.UpdatePlayState(fixture.Item, fixture.Data, 95L)).Returns(completed);
        var session = await fixture.OpenSession();

        await fixture.Manager.OnPlaybackStopped(fixture.Stop(session, "playback-1", failed));

        fixture.Favorites.Verify(
            i => i.RecordPlaybackCompletion(fixture.User, fixture.Item, "playback-1"),
            completed && !failed ? Times.Once() : Times.Never());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("client-playback-1")]
    public async Task MissingPlaybackId_StaysStableThroughDuplicateReportsAndChangesOnReplay(string? clientPlaybackId)
    {
        await using var fixture = new PlaybackFixture();
        fixture.UserData.Setup(i => i.UpdatePlayState(fixture.Item, fixture.Data, 95L)).Returns(true);
        var completionIds = new List<string?>();
        fixture.Favorites
            .Setup(i => i.RecordPlaybackCompletion(fixture.User, fixture.Item, It.IsAny<string?>()))
            .Callback<User, BaseItem, string?>((_, _, completionId) => completionIds.Add(completionId));
        var session = await fixture.OpenSession();

        await fixture.Manager.OnPlaybackStart(fixture.Start(session, clientPlaybackId));
        await fixture.Manager.OnPlaybackProgress(fixture.Progress(session, null));
        await fixture.Manager.OnPlaybackProgress(fixture.Progress(session, null));
        await fixture.Manager.OnPlaybackStopped(fixture.Stop(session, null));
        await fixture.Manager.OnPlaybackStopped(fixture.Stop(session, null));

        await fixture.Manager.OnPlaybackStart(fixture.Start(session, clientPlaybackId is null ? null : "client-playback-2"));
        await fixture.Manager.OnPlaybackProgress(fixture.Progress(session, null));

        Assert.Equal(5, completionIds.Count);
        Assert.False(string.IsNullOrEmpty(completionIds[0]));
        for (var index = 1; index < 4; index++)
        {
            Assert.Equal(completionIds[0], completionIds[index]);
        }

        Assert.NotEqual(completionIds[0], completionIds[4]);
        if (clientPlaybackId is not null)
        {
            Assert.Equal(clientPlaybackId, completionIds[0]);
        }
    }

    [Fact]
    public async Task ExplicitPlaybackId_IsKeptWhenLaterReportsOnlyContainStartTime()
    {
        await using var fixture = new PlaybackFixture();
        fixture.UserData.Setup(i => i.UpdatePlayState(fixture.Item, fixture.Data, 95L)).Returns(true);
        var session = await fixture.OpenSession();
        var start = fixture.Start(session, "client-playback-1");
        start.PlaybackStartTimeTicks = 638900000000000000;
        var progress = fixture.Progress(session, null);
        progress.PlaybackStartTimeTicks = start.PlaybackStartTimeTicks;

        await fixture.Manager.OnPlaybackStart(start);
        await fixture.Manager.OnPlaybackProgress(progress);
        await fixture.Manager.OnPlaybackStopped(fixture.Stop(session, null));

        fixture.Favorites.Verify(i => i.RecordPlaybackCompletion(fixture.User, fixture.Item, "client-playback-1"), Times.Exactly(2));
    }

    [Fact]
    public async Task ReportedStartTime_PreservesLegacyCompletionIdentityAfterSessionManagerRestart()
    {
        var user = new User("favorite-test", "default", "default");
        var item = Mock.Of<BaseItem>();
        item.Id = Guid.NewGuid();
        var completionIds = new List<string?>();

        for (var restart = 0; restart < 2; restart++)
        {
            await using var fixture = new PlaybackFixture(user, item);
            fixture.UserData.Setup(i => i.UpdatePlayState(item, fixture.Data, 95L)).Returns(true);
            fixture.Favorites
                .Setup(i => i.RecordPlaybackCompletion(user, item, It.IsAny<string?>()))
                .Callback<User, BaseItem, string?>((_, _, completionId) => completionIds.Add(completionId));
            var session = await fixture.OpenSession();
            var progress = fixture.Progress(session, null);
            progress.PlaybackStartTimeTicks = 638900000000000000;

            await fixture.Manager.OnPlaybackProgress(progress);
            await fixture.Manager.OnPlaybackStopped(fixture.Stop(session, null));
        }

        Assert.Equal(4, completionIds.Count);
        Assert.All(completionIds, completionId => Assert.Equal(completionIds[0], completionId));
    }

    [Fact]
    public async Task UnidentifiedLegacyCompletionAfterRestart_WaitsForANewPlaybackStart()
    {
        var user = new User("favorite-test", "default", "default");
        var item = Mock.Of<BaseItem>();
        item.Id = Guid.NewGuid();
        await using (var beforeRestart = new PlaybackFixture(user, item))
        {
            beforeRestart.UserData.Setup(i => i.UpdatePlayState(item, beforeRestart.Data, 95L)).Returns(true);
            var originalSession = await beforeRestart.OpenSession();
            await beforeRestart.Manager.OnPlaybackStart(beforeRestart.Start(originalSession, null));
            await beforeRestart.Manager.OnPlaybackProgress(beforeRestart.Progress(originalSession, null));
            beforeRestart.Favorites.Verify(i => i.RecordPlaybackCompletion(user, item, It.IsAny<string?>()), Times.Once());
        }

        await using var afterRestart = new PlaybackFixture(user, item);
        afterRestart.Data.Played = true;
        afterRestart.Data.IsFavorite = true;
        afterRestart.UserData.Setup(i => i.UpdatePlayState(item, afterRestart.Data, 95L)).Returns(true);
        var session = await afterRestart.OpenSession();

        await afterRestart.Manager.OnPlaybackProgress(afterRestart.Progress(session, null));
        await afterRestart.Manager.OnPlaybackStopped(afterRestart.Stop(session, null));
        afterRestart.Favorites.Verify(i => i.RecordPlaybackCompletion(user, item, It.IsAny<string?>()), Times.Never());

        await afterRestart.Manager.OnPlaybackStart(afterRestart.Start(session, null));
        await afterRestart.Manager.OnPlaybackProgress(afterRestart.Progress(session, null));
        await afterRestart.Manager.OnPlaybackStopped(afterRestart.Stop(session, null));

        afterRestart.Favorites.Verify(
            i => i.RecordPlaybackCompletion(user, item, It.Is<string?>(id => !string.IsNullOrEmpty(id))),
            Times.Exactly(2));
    }

    private sealed class PlaybackFixture : IAsyncDisposable
    {
        public PlaybackFixture(User? user = null, BaseItem? item = null)
        {
            User = user ?? new User("favorite-test", "default", "default");
            Item = item ?? Mock.Of<BaseItem>();
            if (item is null)
            {
                Item.Id = Guid.NewGuid();
            }

            UserData.Setup(i => i.GetUserData(User, Item)).Returns(Data);
            var users = new Mock<IUserManager>();
            users.Setup(i => i.GetUserById(User.Id)).Returns(User);
            var library = new Mock<ILibraryManager>();
            library.Setup(i => i.GetItemById(Item.Id)).Returns(Item);
            var configuration = new Mock<IServerConfigurationManager>();
            configuration.SetupGet(i => i.Configuration).Returns(new ServerConfiguration());
            Manager = new Emby.Server.Implementations.Session.SessionManager(
                NullLogger<Emby.Server.Implementations.Session.SessionManager>.Instance,
                Mock.Of<IEventManager>(),
                UserData.Object,
                configuration.Object,
                library.Object,
                users.Object,
                Mock.Of<IMusicManager>(),
                Mock.Of<IDtoService>(),
                Mock.Of<IImageProcessor>(),
                Mock.Of<IServerApplicationHost>(),
                Mock.Of<IDeviceManager>(),
                Mock.Of<IMediaSourceManager>(),
                Mock.Of<IHostApplicationLifetime>(),
                Favorites.Object);
        }

        public User User { get; }

        public BaseItem Item { get; }

        public UserItemData Data { get; } = new() { Key = "favorite-test" };

        public Mock<IUserDataManager> UserData { get; } = new();

        public Mock<IFavoriteLifecycleService> Favorites { get; } = new();

        public Emby.Server.Implementations.Session.SessionManager Manager { get; }

        public Task<SessionInfo> OpenSession()
            => Manager.LogSessionActivity("Favorite Test", "1.0.0", "favorite-device", "Test", "127.0.0.1", User);

        public PlaybackStartInfo Start(SessionInfo session, string? playbackId)
            => new()
            {
                SessionId = session.Id,
                ItemId = Item.Id,
                Item = new BaseItemDto { Id = Item.Id },
                PlaySessionId = playbackId!,
                IsPaused = true
            };

        public PlaybackProgressInfo Progress(SessionInfo session, string? playbackId)
            => new()
            {
                SessionId = session.Id,
                ItemId = Item.Id,
                Item = new BaseItemDto { Id = Item.Id },
                PlaySessionId = playbackId!,
                PositionTicks = 95,
                IsPaused = true
            };

        public PlaybackStopInfo Stop(SessionInfo session, string? playbackId, bool failed = false)
            => new()
            {
                SessionId = session.Id,
                ItemId = Item.Id,
                Item = new BaseItemDto { Id = Item.Id },
                PlaySessionId = playbackId!,
                PositionTicks = 95,
                Failed = failed
            };

        public ValueTask DisposeAsync() => Manager.DisposeAsync();
    }
}
