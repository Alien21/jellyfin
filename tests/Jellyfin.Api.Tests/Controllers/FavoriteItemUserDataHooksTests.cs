using System;
using System.Collections.Generic;
using System.Security.Claims;
using Jellyfin.Api.Constants;
using Jellyfin.Api.Controllers;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Api.Tests.Controllers;

public class FavoriteItemUserDataHooksTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FavoriteChangesWithPlayedSnapshot_DoNotCountAsNewViewing(bool isFavorite)
    {
        var fixture = new Fixture();
        var update = new UpdateUserItemDataDto { IsFavorite = isFavorite, Played = true };

        var result = fixture.Controller.UpdateItemUserData(fixture.User.Id, fixture.Item.Id, update);

        Assert.Same(fixture.Response, result.Value);
        Assert.Equal(new[] { "save", "favorite" }, fixture.Calls);
        Assert.Equal(isFavorite, update.IsFavorite);
        Assert.True(update.Played);
        fixture.Favorites.Verify(value => value.SetFavorite(fixture.User, fixture.Item, isFavorite), Times.Once());
        fixture.Favorites.Verify(value => value.RecordMarkedPlayed(It.IsAny<User>(), It.IsAny<BaseItem>()), Times.Never());
    }

    [Fact]
    public void StandalonePlayedTrue_RecordsNewManualViewingOfAlreadyPlayedItemAfterSave()
    {
        var fixture = new Fixture();
        var update = new UpdateUserItemDataDto { Played = true };

        var result = fixture.Controller.UpdateItemUserData(fixture.User.Id, fixture.Item.Id, update);

        Assert.Same(fixture.Response, result.Value);
        Assert.True(fixture.Response.Played);
        Assert.Equal(new[] { "save", "watched" }, fixture.Calls);
        fixture.Favorites.Verify(value => value.RecordMarkedPlayed(fixture.User, fixture.Item), Times.Once());
        fixture.Favorites.Verify(value => value.SetFavorite(It.IsAny<User>(), It.IsAny<BaseItem>(), It.IsAny<bool>()), Times.Never());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public void UnplayedOrUnspecifiedPlayed_DoesNotCountAsNewViewing(bool? played)
    {
        var fixture = new Fixture();

        fixture.Controller.UpdateItemUserData(fixture.User.Id, fixture.Item.Id, new UpdateUserItemDataDto { Played = played });

        Assert.Equal(new[] { "save" }, fixture.Calls);
        fixture.Favorites.Verify(value => value.RecordMarkedPlayed(It.IsAny<User>(), It.IsAny<BaseItem>()), Times.Never());
    }

    private sealed class Fixture
    {
        public Fixture()
        {
            var users = new Mock<IUserManager>();
            users.Setup(value => value.GetUserById(User.Id)).Returns(User);
            var library = new Mock<ILibraryManager>();
            library.Setup(value => value.GetItemById<BaseItem>(Item.Id, User)).Returns(Item);
            var userData = new Mock<IUserDataManager>();
            userData.Setup(value => value.GetUserData(User, Item)).Returns(new UserItemData { Key = "favorite-test", Played = true });
            userData.Setup(value => value.GetUserDataDto(Item, User)).Returns(Response);
            userData.Setup(value => value.SaveUserData(User, Item, It.IsAny<UpdateUserItemDataDto>(), UserDataSaveReason.UpdateUserData))
                .Callback((User user, BaseItem item, UpdateUserItemDataDto update, UserDataSaveReason reason) =>
                {
                    Assert.Null(update.IsFavorite);
                    Calls.Add("save");
                });
            Favorites.Setup(value => value.SetFavorite(User, Item, It.IsAny<bool>())).Callback(() => Calls.Add("favorite"));
            Favorites.Setup(value => value.RecordMarkedPlayed(User, Item)).Callback(() => Calls.Add("watched"));
            Controller = new ItemsController(
                users.Object,
                library.Object,
                Mock.Of<ILocalizationManager>(),
                Mock.Of<IDtoService>(),
                NullLogger<ItemsController>.Instance,
                Mock.Of<ISessionManager>(),
                userData.Object,
                Mock.Of<ISearchManager>(),
                Favorites.Object)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext
                    {
                        User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(InternalClaimTypes.UserId, User.Id.ToString()) }))
                    }
                }
            };
        }

        public User User { get; } = new("favorite-test", "default", "default") { Id = Guid.NewGuid(), EnableUserPreferenceAccess = true };

        public Episode Item { get; } = new() { Id = Guid.NewGuid() };

        public UserItemDataDto Response { get; } = new() { Key = "favorite-test", Played = true };

        public List<string> Calls { get; } = new();

        public Mock<IFavoriteLifecycleService> Favorites { get; } = new();

        public ItemsController Controller { get; }
    }
}
