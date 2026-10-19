using System;
using System.Collections.Generic;
using System.Net;
using System.Security.Claims;
using System.Threading.Tasks;
using Jellyfin.Api.Constants;
using Jellyfin.Api.Controllers;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Session;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Api.Tests.Controllers;

public class FavoritePlaystateHooksTests
{
    [Fact]
    public async Task ExplicitSeriesMarkPlayed_RecordsTheRootAfterMutationAndUnplayedDoesNotCountAsViewing()
    {
        var user = new User("favorite-test", "default", "default");
        var series = new Mock<Series>();
        series.Object.Id = Guid.NewGuid();
        var calls = new List<string>();
        series.Setup(i => i.MarkUnplayed(user)).Callback(() => calls.Add("unplayed"));
        series.Setup(i => i.MarkPlayed(user, null, true)).Callback(() => calls.Add("played"));
        var favorites = new Mock<IFavoriteLifecycleService>();
        favorites.Setup(i => i.RecordMarkedPlayed(user, series.Object)).Callback(() => calls.Add("favorite"));
        var users = new Mock<IUserManager>();
        users.Setup(i => i.GetUserById(user.Id)).Returns(user);
        var library = new Mock<ILibraryManager>();
        library.Setup(i => i.GetItemById<BaseItem>(series.Object.Id, user)).Returns(series.Object);
        var sessions = new Mock<ISessionManager>();
        await using var session = new SessionInfo(sessions.Object, NullLogger.Instance);
        sessions.Setup(i => i.LogSessionActivity(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), user)).ReturnsAsync(session);
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(InternalClaimTypes.UserId, user.Id.ToString()) }))
        };
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        var controller = new PlaystateController(
            users.Object,
            Mock.Of<IUserDataManager>(),
            library.Object,
            sessions.Object,
            NullLoggerFactory.Instance,
            Mock.Of<ITranscodeManager>(),
            favorites.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };

        await controller.MarkUnplayedItem(user.Id, series.Object.Id);
        await controller.MarkPlayedItem(user.Id, series.Object.Id, null);

        Assert.Equal(new[] { "unplayed", "played", "favorite" }, calls);
        favorites.Verify(i => i.RecordMarkedPlayed(user, series.Object), Times.Once());
    }
}
