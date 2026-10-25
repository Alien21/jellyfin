using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Data.Events;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Extensions;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.EntryPoints;

/// <summary>
/// A <see cref="IHostedService"/> responsible for notifying users when libraries are updated.
/// </summary>
public sealed class LibraryChangedNotifier : IHostedService, IDisposable
{
    // A batch holds a live reference to every item it names, so it has to stay small enough that a
    // library scan - which changes items faster than any batch window closes - cannot grow it without
    // bound. Reached only by a scan; interactive use closes a batch on the window long before this.
    internal const int MaxBatchSize = 2000;

    private readonly ILibraryManager _libraryManager;
    private readonly IServerConfigurationManager _configurationManager;
    private readonly IProviderManager _providerManager;
    private readonly ISessionManager _sessionManager;
    private readonly IUserManager _userManager;
    private readonly ILogger<LibraryChangedNotifier> _logger;

    private readonly Lock _libraryChangedSyncLock = new();
    private readonly Dictionary<Guid, Folder> _foldersAddedTo = [];
    private readonly Dictionary<Guid, Folder> _foldersRemovedFrom = [];
    private readonly Dictionary<Guid, BaseItem> _itemsAdded = [];
    private readonly Dictionary<Guid, BaseItem> _itemsRemoved = [];
    private readonly Dictionary<Guid, BaseItem> _itemsUpdated = [];
    private readonly ConcurrentDictionary<Guid, ProgressDispatchState> _progressDispatches = new();

    private Timer? _libraryUpdateTimer;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryChangedNotifier"/> class.
    /// </summary>
    /// <param name="libraryManager">The <see cref="ILibraryManager"/>.</param>
    /// <param name="configurationManager">The <see cref="IServerConfigurationManager"/>.</param>
    /// <param name="sessionManager">The <see cref="ISessionManager"/>.</param>
    /// <param name="userManager">The <see cref="IUserManager"/>.</param>
    /// <param name="logger">The <see cref="ILogger"/>.</param>
    /// <param name="providerManager">The <see cref="IProviderManager"/>.</param>
    public LibraryChangedNotifier(
        ILibraryManager libraryManager,
        IServerConfigurationManager configurationManager,
        ISessionManager sessionManager,
        IUserManager userManager,
        ILogger<LibraryChangedNotifier> logger,
        IProviderManager providerManager)
    {
        _libraryManager = libraryManager;
        _configurationManager = configurationManager;
        _sessionManager = sessionManager;
        _userManager = userManager;
        _logger = logger;
        _providerManager = providerManager;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded += OnLibraryItemAdded;
        _libraryManager.ItemUpdated += OnLibraryItemUpdated;
        _libraryManager.ItemRemoved += OnLibraryItemRemoved;

        _providerManager.RefreshCompleted += OnProviderRefreshCompleted;
        _providerManager.RefreshStarted += OnProviderRefreshStarted;
        _providerManager.RefreshProgress += OnProviderRefreshProgress;
        _sessionManager.SessionControllerConnected += OnSessionControllerConnected;

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded -= OnLibraryItemAdded;
        _libraryManager.ItemUpdated -= OnLibraryItemUpdated;
        _libraryManager.ItemRemoved -= OnLibraryItemRemoved;

        _providerManager.RefreshCompleted -= OnProviderRefreshCompleted;
        _providerManager.RefreshStarted -= OnProviderRefreshStarted;
        _providerManager.RefreshProgress -= OnProviderRefreshProgress;
        _sessionManager.SessionControllerConnected -= OnSessionControllerConnected;

        return Task.CompletedTask;
    }

    private void OnProviderRefreshProgress(object? sender, GenericEventArgs<Tuple<BaseItem, double>> e)
    {
        var item = e.Argument.Item1;

        if (!EnableRefreshMessage(item))
        {
            return;
        }

        // Logical library notifications and reconnect snapshots share the same ordered sender.
        if (item is CollectionFolder)
        {
            _ = QueueLibraryRefreshProgressAsync(item.Id);
            return;
        }

        var progress = e.Argument.Item2;
        var dispatch = _progressDispatches.GetOrAdd(item.Id, static _ => new ProgressDispatchState());
        if (dispatch.ShouldThrottle(null, progress))
        {
            return;
        }

        _ = SendRefreshProgressAsync(CreateRefreshProgressData(item.Id, null, progress));
        var collectionFolders = _libraryManager.GetCollectionFolders(item);

        foreach (var collectionFolder in collectionFolders)
        {
            // Tracked operations publish their own logical library events. The legacy physical
            // aggregate would otherwise overwrite their denominator, mode and completed paths.
            if (_providerManager.GetLibraryRefreshProgress(collectionFolder.Id) is not null)
            {
                continue;
            }

            _ = QueueLibraryRefreshProgressAsync(collectionFolder.Id, legacyLibrary: collectionFolder);
        }
    }

    private Task QueueLibraryRefreshProgressAsync(Guid libraryId, List<Guid>? userIds = null, Folder? legacyLibrary = null)
    {
        var dispatch = _progressDispatches.GetOrAdd(libraryId, static _ => new ProgressDispatchState());
        var snapshot = userIds is null ? _providerManager.GetLibraryRefreshProgress(libraryId) : null;
        if (legacyLibrary is not null && snapshot is not null)
        {
            return Task.CompletedTask;
        }

        var progress = snapshot?.Progress ?? legacyLibrary?.GetRefreshProgress() ?? 100;
        return dispatch.Enqueue(
            previous => SendLibraryRefreshProgressAfterAsync(previous, libraryId, userIds, legacyLibrary),
            snapshot,
            progress,
            force: userIds is not null);
    }

    private async Task SendLibraryRefreshProgressAfterAsync(Task previous, Guid libraryId, List<Guid>? userIds, Folder? legacyLibrary)
    {
        try
        {
            await previous.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "A previous library refresh progress send failed");
        }

        try
        {
            // Read after the preceding send, so a delayed replay cannot overwrite a newer live
            // snapshot. Legacy physical aggregates also yield to any newly tracked operation.
            var snapshot = _providerManager.GetLibraryRefreshProgress(libraryId);
            if (legacyLibrary is not null && snapshot is not null)
            {
                return;
            }

            await SendRefreshProgressAsync(CreateRefreshProgressData(libraryId, snapshot, legacyLibrary?.GetRefreshProgress()), userIds).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not prepare refresh progress for library {LibraryId}", libraryId);
        }
    }

    private static Dictionary<string, string> CreateRefreshProgressData(Guid libraryId, LibraryRefreshProgress? snapshot, double? legacyProgress = null)
        => new()
        {
            ["ItemId"] = libraryId.ToString("N", CultureInfo.InvariantCulture),
            ["Progress"] = (snapshot?.Progress ?? legacyProgress ?? 100).ToString(CultureInfo.InvariantCulture),
            ["Status"] = snapshot?.Status ?? (legacyProgress is < 100 ? "Active" : "Idle"),
            ["Mode"] = snapshot?.Mode ?? string.Empty,
            ["CurrentPath"] = (snapshot?.CurrentPath ?? 0).ToString(CultureInfo.InvariantCulture),
            ["TotalPaths"] = (snapshot?.TotalPaths ?? 0).ToString(CultureInfo.InvariantCulture),
            ["OperationId"] = (snapshot?.OperationId ?? Guid.Empty).ToString("N", CultureInfo.InvariantCulture)
        };

    private static bool SameRefreshContext(LibraryRefreshProgress? first, LibraryRefreshProgress? second)
        => first is null
            ? second is null
            : second is not null
                && first.OperationId.Equals(second.OperationId)
                && string.Equals(first.Mode, second.Mode, StringComparison.Ordinal)
                && string.Equals(first.Status, second.Status, StringComparison.Ordinal)
                && first.CurrentPath == second.CurrentPath
                && first.TotalPaths == second.TotalPaths;

    private async Task SendRefreshProgressAsync(Dictionary<string, string> data, List<Guid>? userIds = null)
    {
        try
        {
            if (userIds is null)
            {
                await _sessionManager.SendMessageToAdminSessions(SessionMessageType.RefreshProgress, data, CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                await _sessionManager.SendMessageToUserSessions(userIds, SessionMessageType.RefreshProgress, data, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not send library refresh progress");
        }
    }

    private void OnSessionControllerConnected(object? sender, SessionEventArgs e)
        => _ = ReplayLibraryRefreshProgressAsync(e.SessionInfo);

    private async Task ReplayLibraryRefreshProgressAsync(SessionInfo session)
    {
        try
        {
            if (session.UserId.IsEmpty())
            {
                return;
            }

            var user = _userManager.GetUserById(session.UserId);
            if (user is null || !user.HasPermission(PermissionKind.IsAdministrator))
            {
                return;
            }

            foreach (var library in _libraryManager.GetUserRootFolder().Children.OfType<CollectionFolder>())
            {
                await QueueLibraryRefreshProgressAsync(library.Id, [user.Id]).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not replay library refresh progress for {UserId}", session.UserId);
        }
    }

    private void OnProviderRefreshStarted(object? sender, GenericEventArgs<BaseItem> e)
        => OnProviderRefreshProgress(sender, new GenericEventArgs<Tuple<BaseItem, double>>(new Tuple<BaseItem, double>(e.Argument, 0)));

    private void OnProviderRefreshCompleted(object? sender, GenericEventArgs<BaseItem> e)
    {
        OnProviderRefreshProgress(sender, new GenericEventArgs<Tuple<BaseItem, double>>(new Tuple<BaseItem, double>(e.Argument, 100)));

        if (e.Argument is not CollectionFolder)
        {
            _progressDispatches.TryRemove(e.Argument.Id, out _);
        }
    }

    private static bool EnableRefreshMessage(BaseItem item)
        => item is CollectionFolder || item is Folder { IsRoot: false, IsTopParent: true }
            and not (AggregateFolder or UserRootFolder or UserView or Channel);

    private void OnLibraryItemAdded(object? sender, ItemChangeEventArgs e)
        => OnLibraryChange(e.Item, e.Parent, _itemsAdded, _foldersAddedTo);

    private void OnLibraryItemUpdated(object? sender, ItemChangeEventArgs e)
        => OnLibraryChange(e.Item, e.Parent, _itemsUpdated, null);

    private void OnLibraryItemRemoved(object? sender, ItemChangeEventArgs e)
        => OnLibraryChange(e.Item, e.Parent, _itemsRemoved, _foldersRemovedFrom);

    private void OnLibraryChange(BaseItem item, BaseItem parent, Dictionary<Guid, BaseItem> itemsList, Dictionary<Guid, Folder>? foldersList)
    {
        if (!FilterItem(item))
        {
            return;
        }

        lock (_libraryChangedSyncLock)
        {
            // The window runs from the first change of a batch and is never extended. Extending it on
            // every change would keep a library scan's batch open for the whole scan, and the batch
            // holds the items it names alive, so it would grow to the size of the library.
            if (_libraryUpdateTimer is null)
            {
                var updateDuration = TimeSpan.FromSeconds(_configurationManager.Configuration.LibraryUpdateDuration);
                _libraryUpdateTimer = new Timer(LibraryUpdateTimerCallback, null, updateDuration, Timeout.InfiniteTimeSpan);
            }

            if (foldersList is not null && parent is Folder folder)
            {
                foldersList[folder.Id] = folder;
            }

            itemsList[item.Id] = item;

            // A window long enough to cover a burst still has to give way once the batch is large
            // enough to be worth sending on its own.
            if (_itemsAdded.Count + _itemsRemoved.Count + _itemsUpdated.Count >= MaxBatchSize)
            {
                _libraryUpdateTimer.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan);
            }
        }
    }

    private async void LibraryUpdateTimerCallback(object? state)
    {
        List<Folder> foldersAddedTo;
        List<Folder> foldersRemovedFrom;
        List<BaseItem> itemsUpdated;
        List<BaseItem> itemsAdded;
        List<BaseItem> itemsRemoved;
        lock (_libraryChangedSyncLock)
        {
            foldersAddedTo = _foldersAddedTo.Values.ToList();
            foldersRemovedFrom = _foldersRemovedFrom.Values.ToList();

            itemsUpdated = _itemsUpdated
                .Where(e => !_itemsAdded.ContainsKey(e.Key))
                .Select(e => e.Value)
                .ToList();

            itemsAdded = _itemsAdded.Values.ToList();
            itemsRemoved = _itemsRemoved.Values.ToList();

            if (_libraryUpdateTimer is not null)
            {
                _libraryUpdateTimer.Dispose();
                _libraryUpdateTimer = null;
            }

            _itemsAdded.Clear();
            _itemsRemoved.Clear();
            _itemsUpdated.Clear();
            _foldersAddedTo.Clear();
            _foldersRemovedFrom.Clear();
        }

        if (itemsAdded.Count == 0
            && itemsUpdated.Count == 0
            && itemsRemoved.Count == 0
            && foldersAddedTo.Count == 0
            && foldersRemovedFrom.Count == 0)
        {
            return;
        }

        await SendChangeNotifications(itemsAdded, itemsUpdated, itemsRemoved, foldersAddedTo, foldersRemovedFrom, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task SendChangeNotifications(
        List<BaseItem> itemsAdded,
        List<BaseItem> itemsUpdated,
        List<BaseItem> itemsRemoved,
        List<Folder> foldersAddedTo,
        List<Folder> foldersRemovedFrom,
        CancellationToken cancellationToken)
    {
        var userIds = _sessionManager.Sessions
            .Select(i => i.UserId)
            .Where(i => !i.IsEmpty())
            .Distinct()
            .ToArray();

        foreach (var userId in userIds)
        {
            LibraryUpdateInfo info;

            try
            {
                info = GetLibraryUpdateInfo(itemsAdded, itemsUpdated, itemsRemoved, foldersAddedTo, foldersRemovedFrom, userId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in GetLibraryUpdateInfo");
                return;
            }

            if (info.IsEmpty)
            {
                continue;
            }

            try
            {
                await _sessionManager.SendMessageToUserSessions(
                        new List<Guid> { userId },
                        SessionMessageType.LibraryChanged,
                        info,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending LibraryChanged message");
            }
        }
    }

    private LibraryUpdateInfo GetLibraryUpdateInfo(
        List<BaseItem> itemsAdded,
        List<BaseItem> itemsUpdated,
        List<BaseItem> itemsRemoved,
        List<Folder> foldersAddedTo,
        List<Folder> foldersRemovedFrom,
        Guid userId)
    {
        var user = _userManager.GetUserById(userId);
        ArgumentNullException.ThrowIfNull(user);

        var newAndRemoved = new List<BaseItem>();
        newAndRemoved.AddRange(foldersAddedTo);
        newAndRemoved.AddRange(foldersRemovedFrom);

        var allUserRootChildren = _libraryManager.GetUserRootFolder()
            .GetChildren(user, true)
            .OfType<Folder>()
            .ToList();

        return new LibraryUpdateInfo
        {
            ItemsAdded = itemsAdded.SelectMany(i => TranslatePhysicalItemToUserLibrary(i, user))
                .Select(i => i.Id.ToString("N", CultureInfo.InvariantCulture))
                .Distinct()
                .ToArray(),
            ItemsUpdated = itemsUpdated.SelectMany(i => TranslatePhysicalItemToUserLibrary(i, user))
                .Select(i => i.Id.ToString("N", CultureInfo.InvariantCulture))
                .Distinct()
                .ToArray(),
            ItemsRemoved = itemsRemoved.SelectMany(i => TranslatePhysicalItemToUserLibrary(i, user, true))
                .Select(i => i.Id.ToString("N", CultureInfo.InvariantCulture))
                .Distinct()
                .ToArray(),
            FoldersAddedTo = foldersAddedTo.SelectMany(i => TranslatePhysicalItemToUserLibrary(i, user))
                .Select(i => i.Id.ToString("N", CultureInfo.InvariantCulture))
                .Distinct()
                .ToArray(),
            FoldersRemovedFrom = foldersRemovedFrom.SelectMany(i => TranslatePhysicalItemToUserLibrary(i, user))
                .Select(i => i.Id.ToString("N", CultureInfo.InvariantCulture))
                .Distinct()
                .ToArray(),
            CollectionFolders = GetTopParentIds(newAndRemoved, allUserRootChildren).ToArray()
        };
    }

    private static bool FilterItem(BaseItem item)
    {
        if (!item.IsFolder && !item.HasPathProtocol)
        {
            return false;
        }

        if (item is IItemByName && item is not MusicArtist)
        {
            return false;
        }

        return item.SourceType == SourceType.Library;
    }

    private static IEnumerable<string> GetTopParentIds(List<BaseItem> items, List<Folder> allUserRootChildren)
    {
        var list = new List<string>();

        foreach (var item in items)
        {
            // If the physical root changed, return the user root
            if (item is AggregateFolder)
            {
                continue;
            }

            foreach (var folder in allUserRootChildren)
            {
                list.Add(folder.Id.ToString("N", CultureInfo.InvariantCulture));
            }
        }

        return list.Distinct(StringComparer.Ordinal);
    }

    private T[] TranslatePhysicalItemToUserLibrary<T>(T item, User user, bool includeIfNotFound = false)
        where T : BaseItem
    {
        // If the physical root changed, return the user root
        if (item is AggregateFolder)
        {
            return _libraryManager.GetUserRootFolder() is T t ? new[] { t } : Array.Empty<T>();
        }

        // Return it only if it's in the user's library
        if (includeIfNotFound || item.IsVisibleStandalone(user))
        {
            return new[] { item };
        }

        return Array.Empty<T>();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _libraryUpdateTimer?.Dispose();
        _libraryUpdateTimer = null;
    }

    private sealed class ProgressDispatchState
    {
        private readonly Lock _syncLock = new();
        private Task _tail = Task.CompletedTask;
        private DateTime _lastQueuedAt;
        private LibraryRefreshProgress? _lastSnapshot;

        public bool ShouldThrottle(LibraryRefreshProgress? snapshot, double progress)
        {
            lock (_syncLock)
            {
                return ThrottleProgress(snapshot, progress);
            }
        }

        public Task Enqueue(Func<Task, Task> send, LibraryRefreshProgress? snapshot, double progress, bool force)
        {
            lock (_syncLock)
            {
                if (!force && ThrottleProgress(snapshot, progress))
                {
                    return Task.CompletedTask;
                }

                var previous = _tail;
                // Scheduling after Tail is replaced also keeps reentrant callbacks behind this send.
                _tail = previous.ContinueWith(
                    _ => send(previous),
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default).Unwrap();
                return _tail;
            }
        }

        private bool ThrottleProgress(LibraryRefreshProgress? snapshot, double progress)
        {
            var now = DateTime.UtcNow;
            if (progress > 0 && progress < 100
                && snapshot?.Status is not ("Queued" or "Idle")
                && snapshot is not { CurrentPath: 0 }
                && SameRefreshContext(_lastSnapshot, snapshot)
                && (now - _lastQueuedAt).TotalMilliseconds < 1000)
            {
                return true;
            }

            _lastQueuedAt = now;
            _lastSnapshot = snapshot;
            return false;
        }
    }
}
