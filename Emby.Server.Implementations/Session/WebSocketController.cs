#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Net.WebSocketMessages;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.Session
{
    public sealed class WebSocketController : ISessionController, IAsyncDisposable, IDisposable
    {
        private readonly ILogger<WebSocketController> _logger;
        private readonly ISessionManager _sessionManager;
        private readonly SessionInfo _session;

        private readonly List<IWebSocketConnection> _sockets;
        private readonly ReaderWriterLockSlim _socketsLock;
        private bool _disposed = false;

        public WebSocketController(
            ILogger<WebSocketController> logger,
            SessionInfo session,
            ISessionManager sessionManager)
        {
            _logger = logger;
            _session = session;
            _sessionManager = sessionManager;
            _sockets = new();
            _socketsLock = new();
        }

        private bool HasOpenSockets
        {
            get
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                try
                {
                    _socketsLock.EnterReadLock();
                    return _sockets.Any(i => i.State == WebSocketState.Open);
                }
                finally
                {
                    _socketsLock.ExitReadLock();
                }
            }
        }

        /// <inheritdoc />
        public bool SupportsMediaControl => HasOpenSockets;

        /// <inheritdoc />
        public bool IsSessionActive => HasOpenSockets;

        public void AddWebSocket(IWebSocketConnection connection)
        {
            _logger.LogDebug("Adding websocket to session {Session}", _session.Id);
            ObjectDisposedException.ThrowIf(_disposed, this);
            try
            {
                _socketsLock.EnterWriteLock();
                _sockets.Add(connection);
                connection.Closed += OnConnectionClosed;
            }
            finally
            {
                _socketsLock.ExitWriteLock();
            }
        }

        private async void OnConnectionClosed(object? sender, EventArgs e)
        {
            var connection = sender as IWebSocketConnection ?? throw new ArgumentException($"{nameof(sender)} is not of type {nameof(IWebSocketConnection)}", nameof(sender));
            _logger.LogDebug("Removing websocket from session {Session}", _session.Id);
            ObjectDisposedException.ThrowIf(_disposed, this);
            try
            {
                _socketsLock.EnterWriteLock();
                _sockets.Remove(connection);
                connection.Closed -= OnConnectionClosed;
            }
            finally
            {
                _socketsLock.ExitWriteLock();
            }

            await _sessionManager.CloseIfNeededAsync(_session).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public Task SendMessage<T>(
            SessionMessageType name,
            Guid messageId,
            T data,
            CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            IWebSocketConnection[] sockets;
            try
            {
                _socketsLock.EnterReadLock();
                var openSockets = _sockets.Where(i => i.State == WebSocketState.Open);
                if (IsStateNotification(name))
                {
                    // Browser tabs share a session and each needs its own state notifications.
                    sockets = openSockets.ToArray();
                }
                else
                {
                    // Playback and SyncPlay control apply to a single active player.
                    var socket = openSockets.MaxBy(i => i.LastActivityDate);
                    sockets = socket is null ? [] : [socket];
                }
            }
            finally
            {
                _socketsLock.ExitReadLock();
            }

            if (sockets.Length == 0)
            {
                return Task.CompletedTask;
            }

            var message = new OutboundWebSocketMessage<T>
            {
                Data = data,
                MessageType = name,
                MessageId = messageId
            };

            if (sockets.Length == 1)
            {
                return sockets[0].SendAsync(message, cancellationToken);
            }

            // Capture synchronous send failures as well so every connection is attempted.
            return Task.WhenAll(sockets.Select(async socket =>
                await socket.SendAsync(message, cancellationToken).ConfigureAwait(false)));
        }

        private static bool IsStateNotification(SessionMessageType name)
            => name is SessionMessageType.UserDataChanged
                or SessionMessageType.Sessions
                or SessionMessageType.RestartRequired
                or SessionMessageType.ServerShuttingDown
                or SessionMessageType.ServerRestarting
                or SessionMessageType.LibraryChanged
                or SessionMessageType.UserDeleted
                or SessionMessageType.UserUpdated
                or SessionMessageType.SeriesTimerCreated
                or SessionMessageType.TimerCreated
                or SessionMessageType.SeriesTimerCancelled
                or SessionMessageType.TimerCancelled
                or SessionMessageType.RefreshProgress
                or SessionMessageType.ScheduledTaskEnded
                or SessionMessageType.PackageInstallationCancelled
                or SessionMessageType.PackageInstallationFailed
                or SessionMessageType.PackageInstallationCompleted
                or SessionMessageType.PackageInstalling
                or SessionMessageType.PackageUninstalled
                or SessionMessageType.ActivityLogEntry
                or SessionMessageType.ScheduledTasksInfo;

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                _socketsLock.EnterWriteLock();
                foreach (var socket in _sockets)
                {
                    socket.Closed -= OnConnectionClosed;
                    socket.Dispose();
                }

                _sockets.Clear();
            }
            finally
            {
                _socketsLock.ExitWriteLock();
            }

            _socketsLock.Dispose();
            _disposed = true;
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                _socketsLock.EnterWriteLock();
                foreach (var socket in _sockets)
                {
                    socket.Closed -= OnConnectionClosed;
                    await socket.DisposeAsync().ConfigureAwait(false);
                }

                _sockets.Clear();
            }
            finally
            {
                _socketsLock.ExitWriteLock();
            }

            _socketsLock.Dispose();
            _disposed = true;
        }
    }
}
