using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.EntryPoints;
using Jellyfin.Data;
using Jellyfin.Data.Events;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.EntryPoints;

public class LibraryChangedNotifierTests
{
    // How long a test waits for the notifier's timer callback to run. Generous: the assertions are
    // about a batch being sent at all, not about how promptly.
    private static readonly TimeSpan _flushTimeout = TimeSpan.FromSeconds(15);

    private readonly Mock<ILibraryManager> _libraryManager = new();
    private readonly Mock<IServerConfigurationManager> _configurationManager = new();
    private readonly Mock<ISessionManager> _sessionManager = new();
    private readonly Mock<IUserManager> _userManager = new();
    private readonly Mock<IProviderManager> _providerManager = new();
    private readonly ServerConfiguration _configuration = new();

    private int _flushCount;

    public LibraryChangedNotifierTests()
    {
        _configurationManager.SetupGet(e => e.Configuration).Returns(_configuration);

        // Reading the session list is the first thing a flush does, so it stands in for "a batch was
        // sent" without having to mock a whole user library behind it.
        _sessionManager.SetupGet(e => e.Sessions)
            .Returns(() =>
            {
                Interlocked.Increment(ref _flushCount);
                return [];
            });
    }

    [Fact]
    public async Task OnLibraryItemUpdated_BatchSizeCapReached_SendsWithoutWaitingForWindow()
    {
        // Long enough that only the size cap can close the batch.
        _configuration.LibraryUpdateDuration = 3600;

        var notifier = CreateNotifier();
        await notifier.StartAsync(TestContext.Current.CancellationToken);

        for (var i = 0; i < LibraryChangedNotifier.MaxBatchSize; i++)
        {
            RaiseItemUpdated();
        }

        Assert.True(await WaitForFlushAsync(1), "The batch was not sent once it hit the size cap.");

        await notifier.StopAsync(TestContext.Current.CancellationToken);
        notifier.Dispose();
    }

    [Fact]
    public async Task OnLibraryItemUpdated_ChangesNeverPause_StillSendsOnTheWindow()
    {
        // A scan changes items continuously. The window must run from the first change of a batch, or
        // the batch never closes and holds every item it named alive for the length of the scan.
        _configuration.LibraryUpdateDuration = 1;

        var notifier = CreateNotifier();
        await notifier.StartAsync(TestContext.Current.CancellationToken);

        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < _flushTimeout && Volatile.Read(ref _flushCount) == 0)
        {
            // Well below the window, and well below the size cap over the whole loop.
            RaiseItemUpdated();
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        Assert.True(Volatile.Read(ref _flushCount) > 0, "The batch was never sent while changes kept arriving.");

        await notifier.StopAsync(TestContext.Current.CancellationToken);
        notifier.Dispose();
    }

    [Fact]
    public async Task OnRefreshProgress_QueuedLibrarySendsItsPlanWithoutPhysicalAggregation()
    {
        var library = new CollectionFolder { Id = Guid.NewGuid() };
        var operation = Guid.NewGuid();
        _providerManager.Setup(p => p.GetLibraryRefreshProgress(library.Id)).Returns(new LibraryRefreshProgress
        {
            OperationId = operation, Status = "Queued", Mode = "Sequential", CurrentPath = 0, TotalPaths = 3
        });
        var messages = CaptureAdminProgress();
        using var notifier = CreateNotifier();
        await notifier.StartAsync(TestContext.Current.CancellationToken);

        RaiseProgress(library, 0);
        await WaitForMessagesAsync(messages, 1);

        var payload = Assert.Single(messages);
        Assert.Equal(7, payload.Count);
        Assert.Equal(library.Id.ToString("N"), payload["ItemId"]);
        Assert.Equal("0", payload["Progress"]);
        Assert.Equal("Queued", payload["Status"]);
        Assert.Equal("Sequential", payload["Mode"]);
        Assert.Equal("0", payload["CurrentPath"]);
        Assert.Equal("3", payload["TotalPaths"]);
        Assert.Equal(operation.ToString("N"), payload["OperationId"]);
        _libraryManager.Verify(l => l.GetCollectionFolders(It.IsAny<BaseItem>()), Times.Never);
        await notifier.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task OnRefreshProgress_AggregateLibraryUsesTheAuthoritativeWholePlanSnapshot()
    {
        var library = new CollectionFolder { Id = Guid.NewGuid() };
        _providerManager.Setup(p => p.GetLibraryRefreshProgress(library.Id)).Returns(new LibraryRefreshProgress
        {
            OperationId = Guid.NewGuid(), Status = "Active", Mode = "Aggregate", Progress = 100.0 / 3, CurrentPath = 2, TotalPaths = 3
        });
        var messages = CaptureAdminProgress();
        using var notifier = CreateNotifier();
        await notifier.StartAsync(TestContext.Current.CancellationToken);

        RaiseProgress(library, 91);
        await WaitForMessagesAsync(messages, 1);

        var payload = Assert.Single(messages);
        Assert.Equal((100.0 / 3).ToString(CultureInfo.InvariantCulture), payload["Progress"]);
        Assert.Equal("Aggregate", payload["Mode"]);
        Assert.Equal("Active", payload["Status"]);
        Assert.Equal("2", payload["CurrentPath"]);
        Assert.Equal("3", payload["TotalPaths"]);
        await notifier.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task OnRefreshProgress_CompletedLogicalLibraryClearsBothProgressLevels()
    {
        var library = new CollectionFolder { Id = Guid.NewGuid() };
        var messages = CaptureAdminProgress();
        using var notifier = CreateNotifier();
        await notifier.StartAsync(TestContext.Current.CancellationToken);

        RaiseProgress(library, 5);
        await WaitForMessagesAsync(messages, 1);

        var payload = Assert.Single(messages);
        Assert.Equal("100", payload["Progress"]);
        Assert.Equal("Idle", payload["Status"]);
        Assert.Equal(string.Empty, payload["Mode"]);
        Assert.Equal("0", payload["CurrentPath"]);
        Assert.Equal("0", payload["TotalPaths"]);
        Assert.Equal(Guid.Empty.ToString("N"), payload["OperationId"]);
        _libraryManager.Verify(l => l.GetCollectionFolders(It.IsAny<BaseItem>()), Times.Never);
        await notifier.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task OnRefreshProgress_QueuedAndIdleTransitionsBypassIntermediateThrottling()
    {
        var library = new CollectionFolder { Id = Guid.NewGuid() };
        var operation = Guid.NewGuid();
        LibraryRefreshProgress? state = new()
        {
            OperationId = operation, Status = "Active", Progress = 40, CurrentPath = 1, TotalPaths = 3
        };
        _providerManager.Setup(p => p.GetLibraryRefreshProgress(library.Id)).Returns(() => state!);
        var messages = CaptureAdminProgress();
        using var notifier = CreateNotifier();
        await notifier.StartAsync(TestContext.Current.CancellationToken);

        RaiseProgress(library, 40);
        await WaitForMessagesAsync(messages, 1);
        state = new LibraryRefreshProgress { OperationId = operation, Status = "Active", Progress = 50, CurrentPath = 1, TotalPaths = 3 };
        RaiseProgress(library, 50);
        Assert.Single(messages);

        state = new LibraryRefreshProgress { Status = "Queued", CurrentPath = 0, TotalPaths = 3 };
        RaiseProgress(library, 0);
        await WaitForMessagesAsync(messages, 2);
        state = null;
        RaiseProgress(library, 100);
        await WaitForMessagesAsync(messages, 3);

        Assert.Equal(new[] { "Active", "Queued", "Idle" }, messages.Select(payload => payload["Status"]));
        Assert.Equal(new[] { "40", "0", "100" }, messages.Select(payload => payload["Progress"]));
        await notifier.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task OnRefreshProgress_AggregatePathTransitionsBypassIntermediateThrottling()
    {
        var library = new CollectionFolder { Id = Guid.NewGuid() };
        var operation = Guid.NewGuid();
        LibraryRefreshProgress state = new()
        {
            OperationId = operation, Status = "Active", Mode = "Aggregate", Progress = 20.0 / 3, CurrentPath = 1, TotalPaths = 3
        };
        _providerManager.Setup(p => p.GetLibraryRefreshProgress(library.Id)).Returns(() => state);
        var messages = CaptureAdminProgress();
        using var notifier = CreateNotifier();
        await notifier.StartAsync(TestContext.Current.CancellationToken);

        RaiseProgress(library, state.Progress);
        await WaitForMessagesAsync(messages, 1);
        state = new LibraryRefreshProgress
        {
            OperationId = operation, Status = "Active", Mode = "Aggregate", Progress = 20.0 / 3, CurrentPath = 2, TotalPaths = 3
        };
        RaiseProgress(library, state.Progress);
        await WaitForMessagesAsync(messages, 2);
        state = new LibraryRefreshProgress
        {
            OperationId = operation, Status = "Active", Mode = "Aggregate", Progress = 10, CurrentPath = 2, TotalPaths = 3
        };
        RaiseProgress(library, state.Progress);

        Assert.Equal(2, messages.Count);
        Assert.Equal(new[] { "1", "2" }, messages.Select(payload => payload["CurrentPath"]));
        Assert.Equal(messages[0]["Progress"], messages[1]["Progress"]);
        await notifier.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task OnRefreshProgress_AlreadyActiveOperationAndModeHandoffsBypassThrottling()
    {
        var library = new CollectionFolder { Id = Guid.NewGuid() };
        var firstOperation = Guid.NewGuid();
        var nextOperation = Guid.NewGuid();
        LibraryRefreshProgress state = new()
        {
            OperationId = firstOperation, Status = "Active", Mode = "Aggregate", Progress = 60, CurrentPath = 2, TotalPaths = 3
        };
        _providerManager.Setup(p => p.GetLibraryRefreshProgress(library.Id)).Returns(() => state);
        var messages = CaptureAdminProgress();
        using var notifier = CreateNotifier();
        await notifier.StartAsync(TestContext.Current.CancellationToken);

        RaiseProgress(library, state.Progress);
        await WaitForMessagesAsync(messages, 1);
        state = new LibraryRefreshProgress
        {
            OperationId = nextOperation, Status = "Active", Mode = "Aggregate", Progress = 45, CurrentPath = 2, TotalPaths = 3
        };
        RaiseProgress(library, state.Progress);
        await WaitForMessagesAsync(messages, 2);
        state = new LibraryRefreshProgress
        {
            OperationId = nextOperation, Status = "Active", Mode = "Sequential", Progress = 50, CurrentPath = 2, TotalPaths = 3
        };
        RaiseProgress(library, state.Progress);
        await WaitForMessagesAsync(messages, 3);

        Assert.Equal(3, messages.Count);
        Assert.Equal(new[] { firstOperation.ToString("N"), nextOperation.ToString("N"), nextOperation.ToString("N") }, messages.Select(payload => payload["OperationId"]));
        Assert.Equal(new[] { "Aggregate", "Aggregate", "Sequential" }, messages.Select(payload => payload["Mode"]));
        await notifier.StopAsync(TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnPhysicalRefreshProgress_OnlyUntrackedLibrariesUseTheLegacyAggregate(bool tracked)
    {
        var physical = new TopLevelProgressFolder { Id = Guid.NewGuid() };
        Folder library = tracked
            ? new CollectionFolder { Id = Guid.NewGuid() }
            : new LegacyProgressFolder { Id = Guid.NewGuid() };
        _libraryManager.Setup(l => l.GetCollectionFolders(physical)).Returns([library]);
        if (tracked)
        {
            _providerManager.Setup(p => p.GetLibraryRefreshProgress(library.Id)).Returns(new LibraryRefreshProgress
            {
                OperationId = Guid.NewGuid(), Status = "Active", Mode = "Aggregate", Progress = 60, CurrentPath = 2, TotalPaths = 3
            });
        }

        var messages = CaptureAdminProgress();
        using var notifier = CreateNotifier();
        await notifier.StartAsync(TestContext.Current.CancellationToken);

        RaiseProgress(physical, 37);
        await WaitForMessagesAsync(messages, tracked ? 1 : 2);

        Assert.Equal(tracked ? 1 : 2, messages.Count);
        Assert.Equal(physical.Id.ToString("N"), messages[0]["ItemId"]);
        Assert.Equal("37", messages[0]["Progress"]);
        if (tracked)
        {
            Assert.DoesNotContain(messages, payload => payload["ItemId"] == library.Id.ToString("N"));
        }
        else
        {
            var aggregate = Assert.Single(messages, payload => payload["ItemId"] == library.Id.ToString("N"));
            Assert.Equal("42", aggregate["Progress"]);
            Assert.Equal("Active", aggregate["Status"]);
            Assert.Equal(string.Empty, aggregate["Mode"]);
            Assert.Equal("0", aggregate["TotalPaths"]);
        }

        await notifier.StopAsync(TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnSessionControllerConnected_ReplaysEveryLibraryOnlyToTheAdministrator(bool administrator)
    {
        var user = new User("user", "auth", "reset") { Id = Guid.NewGuid() };
        user.SetPermission(PermissionKind.IsAdministrator, administrator);
        _userManager.Setup(u => u.GetUserById(user.Id)).Returns(user);
        var queued = new CollectionFolder { Id = Guid.NewGuid() };
        var active = new CollectionFolder { Id = Guid.NewGuid() };
        var idle = new CollectionFolder { Id = Guid.NewGuid() };
        _libraryManager.Setup(l => l.GetUserRootFolder()).Returns(new UserRootFolder { Children = [queued, active, idle, new Folder()] });
        _providerManager.Setup(p => p.GetLibraryRefreshProgress(queued.Id)).Returns(new LibraryRefreshProgress
        {
            OperationId = Guid.NewGuid(), Status = "Queued", CurrentPath = 0, TotalPaths = 3
        });
        _providerManager.Setup(p => p.GetLibraryRefreshProgress(active.Id)).Returns(new LibraryRefreshProgress
        {
            OperationId = Guid.NewGuid(), Status = "Active", Mode = "Aggregate", Progress = 60, CurrentPath = 2, TotalPaths = 3
        });
        var messages = CaptureUserProgress();
        using var notifier = CreateNotifier();
        await using var session = new SessionInfo(_sessionManager.Object, NullLogger.Instance) { UserId = user.Id };
        await notifier.StartAsync(TestContext.Current.CancellationToken);

        RaiseSessionConnected(session);
        if (administrator)
        {
            await WaitForMessagesAsync(messages, 3);
        }

        Assert.Equal(administrator ? 3 : 0, messages.Count);
        if (administrator)
        {
            Assert.All(messages, message => Assert.Equal(new[] { user.Id }, message.Users));
            var queuedData = Assert.Single(messages, message => message.Payload["ItemId"] == queued.Id.ToString("N")).Payload;
            Assert.Equal("Queued", queuedData["Status"]);
            Assert.Equal("0", queuedData["CurrentPath"]);
            Assert.Equal("3", queuedData["TotalPaths"]);
            var activeData = Assert.Single(messages, message => message.Payload["ItemId"] == active.Id.ToString("N")).Payload;
            Assert.Equal("Aggregate", activeData["Mode"]);
            Assert.Equal("60", activeData["Progress"]);
            var idleData = Assert.Single(messages, message => message.Payload["ItemId"] == idle.Id.ToString("N")).Payload;
            Assert.Equal("Idle", idleData["Status"]);
            Assert.Equal("100", idleData["Progress"]);
            Assert.Equal("0", idleData["TotalPaths"]);
        }
        else
        {
            _libraryManager.Verify(l => l.GetUserRootFolder(), Times.Never);
        }

        await notifier.StopAsync(TestContext.Current.CancellationToken);
        RaiseSessionConnected(session);
        Assert.Equal(administrator ? 3 : 0, messages.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnSessionControllerConnected_AnonymousAndUnknownUsersReceiveNoReplay(bool anonymous)
    {
        var messages = CaptureUserProgress();
        using var notifier = CreateNotifier();
        await using var session = new SessionInfo(_sessionManager.Object, NullLogger.Instance)
        {
            UserId = anonymous ? Guid.Empty : Guid.NewGuid()
        };
        await notifier.StartAsync(TestContext.Current.CancellationToken);

        RaiseSessionConnected(session);

        Assert.Empty(messages);
        _libraryManager.Verify(l => l.GetUserRootFolder(), Times.Never);
        await notifier.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task OnSessionControllerConnected_FailedSendsDoNotStopTheRemainingReplay()
    {
        var user = new User("admin", "auth", "reset") { Id = Guid.NewGuid() };
        user.SetPermission(PermissionKind.IsAdministrator, true);
        _userManager.Setup(u => u.GetUserById(user.Id)).Returns(user);
        _libraryManager.Setup(l => l.GetUserRootFolder()).Returns(new UserRootFolder
        {
            Children = [new CollectionFolder { Id = Guid.NewGuid() }, new CollectionFolder { Id = Guid.NewGuid() }]
        });
        var attempted = 0;
        _sessionManager.Setup(s => s.SendMessageToUserSessions(
                It.IsAny<List<Guid>>(), SessionMessageType.RefreshProgress, It.IsAny<Dictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .Callback(() => Interlocked.Increment(ref attempted))
            .Returns(Task.FromException(new InvalidOperationException("Connection closed")));
        using var notifier = CreateNotifier();
        await using var session = new SessionInfo(_sessionManager.Object, NullLogger.Instance) { UserId = user.Id };
        await notifier.StartAsync(TestContext.Current.CancellationToken);

        RaiseSessionConnected(session);
        await WaitForProgressAsync(() => Volatile.Read(ref attempted) >= 2);

        Assert.Equal(2, attempted);
        await notifier.StopAsync(TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnSessionControllerConnected_DelayedReplayCannotOverwriteNewerLiveProgress(bool initiallyActive)
    {
        var user = new User("admin", "auth", "reset") { Id = Guid.NewGuid() };
        user.SetPermission(PermissionKind.IsAdministrator, true);
        _userManager.Setup(u => u.GetUserById(user.Id)).Returns(user);
        var library = new CollectionFolder { Id = Guid.NewGuid() };
        _libraryManager.Setup(l => l.GetUserRootFolder()).Returns(new UserRootFolder { Children = [library] });
        var operation = Guid.NewGuid();
        LibraryRefreshProgress? state = initiallyActive
            ? new LibraryRefreshProgress
            {
                OperationId = operation, Status = "Active", Mode = "Sequential", Progress = 30, CurrentPath = 1, TotalPaths = 3
            }
            : null;
        _providerManager.Setup(p => p.GetLibraryRefreshProgress(library.Id)).Returns(() => state!);
        var replayStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReplay = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var messages = CaptureAdminProgress();
        _sessionManager.Setup(s => s.SendMessageToUserSessions(
                It.IsAny<List<Guid>>(), SessionMessageType.RefreshProgress, It.IsAny<Dictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .Returns<List<Guid>, SessionMessageType, Dictionary<string, string>, CancellationToken>(async (_, _, payload, _) =>
            {
                replayStarted.TrySetResult(true);
                await releaseReplay.Task.WaitAsync(_flushTimeout, TestContext.Current.CancellationToken);
                lock (messages)
                {
                    messages.Add(payload);
                }
            });
        using var notifier = CreateNotifier();
        await using var session = new SessionInfo(_sessionManager.Object, NullLogger.Instance) { UserId = user.Id };
        await notifier.StartAsync(TestContext.Current.CancellationToken);

        try
        {
            RaiseSessionConnected(session);
            await replayStarted.Task.WaitAsync(_flushTimeout, TestContext.Current.CancellationToken);
            state = new LibraryRefreshProgress
            {
                OperationId = operation, Status = "Active", Mode = "Sequential", Progress = 40, CurrentPath = 1, TotalPaths = 3
            };
            RaiseProgress(library, 40);

            // The live notification is queued behind the replay's pending delivery.
            Assert.Empty(messages);
        }
        finally
        {
            releaseReplay.TrySetResult(true);
        }

        await WaitForMessagesAsync(messages, 2);
        Assert.Equal(initiallyActive ? "30" : "100", messages[0]["Progress"]);
        Assert.Equal("Active", messages[^1]["Status"]);
        Assert.Equal("40", messages[^1]["Progress"]);
        Assert.Equal("1", messages[^1]["CurrentPath"]);
        Assert.Equal("3", messages[^1]["TotalPaths"]);
        Assert.Equal(operation.ToString("N"), messages[^1]["OperationId"]);
        await notifier.StopAsync(TestContext.Current.CancellationToken);
    }

    private List<Dictionary<string, string>> CaptureAdminProgress()
    {
        var messages = new List<Dictionary<string, string>>();
        _sessionManager.Setup(s => s.SendMessageToAdminSessions(
                SessionMessageType.RefreshProgress, It.IsAny<Dictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .Callback<SessionMessageType, Dictionary<string, string>, CancellationToken>((_, payload, _) =>
            {
                lock (messages)
                {
                    messages.Add(payload);
                }
            })
            .Returns(Task.CompletedTask);
        return messages;
    }

    private List<(List<Guid> Users, Dictionary<string, string> Payload)> CaptureUserProgress()
    {
        var messages = new List<(List<Guid> Users, Dictionary<string, string> Payload)>();
        _sessionManager.Setup(s => s.SendMessageToUserSessions(
                It.IsAny<List<Guid>>(), SessionMessageType.RefreshProgress, It.IsAny<Dictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .Callback<List<Guid>, SessionMessageType, Dictionary<string, string>, CancellationToken>((users, _, payload, _) =>
            {
                lock (messages)
                {
                    messages.Add((users, payload));
                }
            })
            .Returns(Task.CompletedTask);
        return messages;
    }

    private void RaiseProgress(BaseItem item, double progress)
        => _providerManager.Raise(
            p => p.RefreshProgress += null,
            _providerManager.Object,
            new GenericEventArgs<Tuple<BaseItem, double>>(new Tuple<BaseItem, double>(item, progress)));

    private void RaiseSessionConnected(SessionInfo session)
        => _sessionManager.Raise(s => s.SessionControllerConnected += null, _sessionManager.Object, new SessionEventArgs { SessionInfo = session });

    private static Task WaitForMessagesAsync<T>(List<T> messages, int expected)
        => WaitForProgressAsync(() =>
        {
            lock (messages)
            {
                return messages.Count >= expected;
            }
        });

    private static async Task WaitForProgressAsync(Func<bool> completed)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!completed() && stopwatch.Elapsed < _flushTimeout)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.True(completed(), "The refresh progress messages were not delivered.");
    }

    private LibraryChangedNotifier CreateNotifier()
        => new(
            _libraryManager.Object,
            _configurationManager.Object,
            _sessionManager.Object,
            _userManager.Object,
            NullLogger<LibraryChangedNotifier>.Instance,
            _providerManager.Object);

    // A folder passes the notifier's item filter without needing any of BaseItem's static services.
    private void RaiseItemUpdated()
        => _libraryManager.Raise(
            e => e.ItemUpdated += null,
            _libraryManager.Object,
            new ItemChangeEventArgs { Item = new Folder { Id = Guid.NewGuid() } });

    private async Task<bool> WaitForFlushAsync(int expected)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < _flushTimeout)
        {
            if (Volatile.Read(ref _flushCount) >= expected)
            {
                return true;
            }

            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        return false;
    }

    private sealed class TopLevelProgressFolder : Folder
    {
        public override bool IsTopParent => true;
    }

    private sealed class LegacyProgressFolder : Folder
    {
        public override double? GetRefreshProgress() => 42;
    }
}
