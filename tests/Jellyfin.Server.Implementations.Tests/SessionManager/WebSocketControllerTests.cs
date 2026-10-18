using System;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.Session;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Net.WebSocketMessages;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.SessionManager;

public class WebSocketControllerTests
{
    [Fact]
    public async Task SendMessage_LibraryChanged_ReachesEveryOpenSocket()
    {
        var sessionManager = Mock.Of<ISessionManager>();
        await using var session = new SessionInfo(sessionManager, NullLogger.Instance);
        using var controller = CreateController(session, sessionManager);
        var olderSocket = CreateSocket(DateTime.UnixEpoch);
        var newerSocket = CreateSocket(DateTime.UnixEpoch.AddMinutes(1));
        controller.AddWebSocket(olderSocket.Object);
        controller.AddWebSocket(newerSocket.Object);
        var messageId = Guid.NewGuid();
        var data = new LibraryUpdateInfo { ItemsAdded = [Guid.NewGuid().ToString("N")] };
        var cancellationToken = TestContext.Current.CancellationToken;

        await controller.SendMessage(SessionMessageType.LibraryChanged, messageId, data, cancellationToken);

        VerifyLibraryChanged(olderSocket, messageId, data, cancellationToken);
        VerifyLibraryChanged(newerSocket, messageId, data, cancellationToken);
    }

    [Theory]
    [InlineData(SessionMessageType.UserDataChanged)]
    [InlineData(SessionMessageType.Sessions)]
    [InlineData(SessionMessageType.RestartRequired)]
    [InlineData(SessionMessageType.ServerShuttingDown)]
    [InlineData(SessionMessageType.ServerRestarting)]
    [InlineData(SessionMessageType.LibraryChanged)]
    [InlineData(SessionMessageType.UserDeleted)]
    [InlineData(SessionMessageType.UserUpdated)]
    [InlineData(SessionMessageType.SeriesTimerCreated)]
    [InlineData(SessionMessageType.TimerCreated)]
    [InlineData(SessionMessageType.SeriesTimerCancelled)]
    [InlineData(SessionMessageType.TimerCancelled)]
    [InlineData(SessionMessageType.RefreshProgress)]
    [InlineData(SessionMessageType.ScheduledTaskEnded)]
    [InlineData(SessionMessageType.PackageInstallationCancelled)]
    [InlineData(SessionMessageType.PackageInstallationFailed)]
    [InlineData(SessionMessageType.PackageInstallationCompleted)]
    [InlineData(SessionMessageType.PackageInstalling)]
    [InlineData(SessionMessageType.PackageUninstalled)]
    [InlineData(SessionMessageType.ActivityLogEntry)]
    [InlineData(SessionMessageType.ScheduledTasksInfo)]
    public async Task SendMessage_StateNotification_ReachesEveryOpenSocket(SessionMessageType messageType)
    {
        var sessionManager = Mock.Of<ISessionManager>();
        await using var session = new SessionInfo(sessionManager, NullLogger.Instance);
        using var controller = CreateController(session, sessionManager);
        var olderSocket = CreateSocket(DateTime.UnixEpoch);
        var newerSocket = CreateSocket(DateTime.UnixEpoch.AddMinutes(1));
        controller.AddWebSocket(olderSocket.Object);
        controller.AddWebSocket(newerSocket.Object);
        var messageId = Guid.NewGuid();
        const string data = "updated";
        var cancellationToken = TestContext.Current.CancellationToken;

        await controller.SendMessage(messageType, messageId, data, cancellationToken);

        foreach (var socket in new[] { olderSocket, newerSocket })
        {
            socket.Verify(
                connection => connection.SendAsync(
                    It.Is<OutboundWebSocketMessage<string>>(message =>
                        message.MessageType == messageType && message.MessageId.Equals(messageId) && message.Data == data),
                    cancellationToken),
                Times.Once);
        }
    }

    [Fact]
    public async Task SendMessage_LibraryChanged_SkipsClosedSockets()
    {
        var sessionManager = Mock.Of<ISessionManager>();
        await using var session = new SessionInfo(sessionManager, NullLogger.Instance);
        using var controller = CreateController(session, sessionManager);
        var openSocket = CreateSocket(DateTime.UnixEpoch);
        var closedSocket = CreateSocket(DateTime.UnixEpoch.AddMinutes(1), WebSocketState.Closed);
        controller.AddWebSocket(openSocket.Object);
        controller.AddWebSocket(closedSocket.Object);
        var messageId = Guid.NewGuid();
        var data = new LibraryUpdateInfo();
        var cancellationToken = TestContext.Current.CancellationToken;

        await controller.SendMessage(SessionMessageType.LibraryChanged, messageId, data, cancellationToken);

        VerifyLibraryChanged(openSocket, messageId, data, cancellationToken);
        closedSocket.Verify(
            socket => socket.SendAsync(It.IsAny<OutboundWebSocketMessage<LibraryUpdateInfo>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Theory]
    [InlineData(SessionMessageType.Play)]
    [InlineData(SessionMessageType.Playstate)]
    [InlineData(SessionMessageType.GeneralCommand)]
    [InlineData(SessionMessageType.SyncPlayCommand)]
    [InlineData(SessionMessageType.SyncPlayGroupUpdate)]
    [InlineData(SessionMessageType.KeepAlive)]
    [InlineData(SessionMessageType.ForceKeepAlive)]
    public async Task SendMessage_ControlMessage_ReachesOnlyMostRecentlyActiveSocket(SessionMessageType messageType)
    {
        var sessionManager = Mock.Of<ISessionManager>();
        await using var session = new SessionInfo(sessionManager, NullLogger.Instance);
        using var controller = CreateController(session, sessionManager);
        var olderSocket = CreateSocket(DateTime.UnixEpoch);
        var newerSocket = CreateSocket(DateTime.UnixEpoch.AddMinutes(1));
        controller.AddWebSocket(olderSocket.Object);
        controller.AddWebSocket(newerSocket.Object);
        var messageId = Guid.NewGuid();
        const string data = "command";
        var cancellationToken = TestContext.Current.CancellationToken;

        await controller.SendMessage(messageType, messageId, data, cancellationToken);

        olderSocket.Verify(
            socket => socket.SendAsync(It.IsAny<OutboundWebSocketMessage<string>>(), It.IsAny<CancellationToken>()),
            Times.Never);
        newerSocket.Verify(
            socket => socket.SendAsync(
                It.Is<OutboundWebSocketMessage<string>>(message =>
                    message.MessageType == messageType && message.MessageId.Equals(messageId) && message.Data == data),
                cancellationToken),
            Times.Once);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SendMessage_LibraryChanged_FailingSocketDoesNotPreventOtherDelivery(bool failSynchronously)
    {
        var sessionManager = Mock.Of<ISessionManager>();
        await using var session = new SessionInfo(sessionManager, NullLogger.Instance);
        using var controller = CreateController(session, sessionManager);
        var failingSocket = CreateSocket(DateTime.UnixEpoch.AddMinutes(1));
        var healthySocket = CreateSocket(DateTime.UnixEpoch);
        var send = failingSocket.Setup(
            socket => socket.SendAsync(It.IsAny<OutboundWebSocketMessage<LibraryUpdateInfo>>(), It.IsAny<CancellationToken>()));
        if (failSynchronously)
        {
            send.Throws(new InvalidOperationException("The socket closed before sending."));
        }
        else
        {
            send.Returns(Task.FromException(new InvalidOperationException("The socket closed while sending.")));
        }

        // The failed socket comes first so a synchronous failure cannot short-circuit the fanout.
        controller.AddWebSocket(failingSocket.Object);
        controller.AddWebSocket(healthySocket.Object);
        var messageId = Guid.NewGuid();
        var data = new LibraryUpdateInfo();
        var cancellationToken = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => controller.SendMessage(SessionMessageType.LibraryChanged, messageId, data, cancellationToken));

        VerifyLibraryChanged(healthySocket, messageId, data, cancellationToken);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SendMessage_LibraryChanged_NoOpenSocketCompletesWithoutSending(bool addClosedSocket)
    {
        var sessionManager = Mock.Of<ISessionManager>();
        await using var session = new SessionInfo(sessionManager, NullLogger.Instance);
        using var controller = CreateController(session, sessionManager);
        var closedSocket = CreateSocket(DateTime.UnixEpoch, WebSocketState.Closed);
        if (addClosedSocket)
        {
            controller.AddWebSocket(closedSocket.Object);
        }

        await controller.SendMessage(
            SessionMessageType.LibraryChanged,
            Guid.NewGuid(),
            new LibraryUpdateInfo(),
            TestContext.Current.CancellationToken);

        closedSocket.Verify(
            socket => socket.SendAsync(It.IsAny<OutboundWebSocketMessage<LibraryUpdateInfo>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private static WebSocketController CreateController(SessionInfo session, ISessionManager sessionManager)
        => new(NullLogger<WebSocketController>.Instance, session, sessionManager);

    private static Mock<IWebSocketConnection> CreateSocket(DateTime lastActivityDate, WebSocketState state = WebSocketState.Open)
    {
        var socket = new Mock<IWebSocketConnection>();
        socket.SetupGet(connection => connection.State).Returns(state);
        socket.SetupGet(connection => connection.LastActivityDate).Returns(lastActivityDate);
        socket.Setup(connection => connection.SendAsync(It.IsAny<OutboundWebSocketMessage<LibraryUpdateInfo>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        socket.Setup(connection => connection.SendAsync(It.IsAny<OutboundWebSocketMessage<string>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return socket;
    }

    private static void VerifyLibraryChanged(
        Mock<IWebSocketConnection> socket,
        Guid messageId,
        LibraryUpdateInfo data,
        CancellationToken cancellationToken)
        => socket.Verify(
            connection => connection.SendAsync(
                It.Is<OutboundWebSocketMessage<LibraryUpdateInfo>>(message =>
                    message.MessageType == SessionMessageType.LibraryChanged
                    && message.MessageId.Equals(messageId)
                    && ReferenceEquals(message.Data, data)),
                cancellationToken),
            Times.Once);
}
