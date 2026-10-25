using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LibraryTaskScheduler;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Controller.Tests.Library;

[Collection(nameof(LibraryScanProgressTests))]
public class LibraryScanProgressTests
{
    [Fact]
    public void PlannedPaths_AreFrozenAndIgnoreUnrelatedOrFinishedReports()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var unrelated = Guid.NewGuid();
        var paths = new List<Guid> { first, second, first };
        var started = new List<Guid>();
        var completed = new List<Guid>();
        var reports = new List<(Guid Path, double Percent)>();
        var overall = new List<double>();
        var progress = new LibraryScanProgress(new InlineProgress(overall.Add), paths, started.Add, (path, percent) => reports.Add((path, percent)), completed.Add);
        paths.Clear();
        paths.Add(unrelated);

        progress.StartPath(first);
        progress.StartPath(first);
        progress.ReportPath(first, 25);
        progress.StartPath(unrelated);
        progress.ReportPath(unrelated, 90);
        progress.CompletePath(unrelated);
        progress.CompletePath(first);
        progress.ReportPath(first, 50);
        progress.StartPath(first);
        progress.CompletePath(first);
        progress.Report(75);

        Assert.Equal([first], started);
        Assert.Equal([(first, 25.0)], reports);
        Assert.Equal([first], completed);
        Assert.Equal([75.0], overall);
    }

    [Fact]
    public void SkippedPaths_AreFinishedWithoutChangingAccessiblePaths()
    {
        var accessible = Guid.NewGuid();
        var skipped = Guid.NewGuid();
        var started = new HashSet<Guid>();
        var completed = new List<Guid>();
        var startedBeforeCompletion = false;
        var progress = new LibraryScanProgress(
            new InlineProgress(_ => { }),
            [accessible, skipped],
            path => started.Add(path),
            (_, _) => { },
            path =>
            {
                startedBeforeCompletion = started.Contains(path);
                completed.Add(path);
            });

        progress.CompleteSkippedPaths([accessible]);
        progress.CompleteSkippedPaths([accessible]);

        Assert.Equal([skipped], completed);
        Assert.Equal([skipped], started);
        Assert.True(startedBeforeCompletion);
    }

    [Fact]
    public async Task RootScan_TracksValidationAndMetadataWithPendingAndCompletedPaths()
    {
        var previousLibrary = BaseItem.LibraryManager;
        var previousRepository = BaseItem.ItemRepository;
        var previousProvider = BaseItem.ProviderManager;
        var previousLogger = BaseItem.Logger;
        var previousMediaSource = BaseItem.MediaSourceManager;
        var previousScheduler = Folder.LimitedConcurrencyLibraryScheduler;
        var previousSynchronizationContext = SynchronizationContext.Current;
        var first = new ScanPathFolder { Id = Guid.NewGuid(), Path = "/media/first" };
        var second = new ScanPathFolder { Id = Guid.NewGuid(), Path = "/media/second" };
        var skipped = Guid.NewGuid();
        var root = new ScanRootFolder(first, second) { Id = Guid.NewGuid(), Path = "/media", IsRoot = true };
        var paths = new Dictionary<Guid, double> { [first.Id] = 0, [second.Id] = 0, [skipped] = 0 };
        var snapshots = new List<double[]>();
        var validationSnapshots = new List<double[]>();
        var started = new List<Guid>();
        var library = new Mock<ILibraryManager>();
        library.Setup(manager => manager.GetItemById(root.Id)).Returns(root);
        var repository = new Mock<IItemRepository>();
        repository.Setup(value => value.GetItemList(It.IsAny<InternalItemsQuery>())).Returns([first, second]);
        var mediaSource = new Mock<IMediaSourceManager>();
        mediaSource.Setup(value => value.GetPathProtocol(It.IsAny<string>())).Returns(MediaProtocol.File);
        var directory = new Mock<IDirectoryService>();
        directory.Setup(value => value.IsAccessible(It.IsAny<string>())).Returns(true);
        first.SetParent(root);
        second.SetParent(root);
        first.ValidationReported = _ => validationSnapshots.Add([paths[first.Id], paths[second.Id], paths[skipped]]);
        first.MetadataStarted = _ => snapshots.Add([paths[first.Id], paths[second.Id], paths[skipped]]);
        first.MetadataReported = _ => snapshots.Add([paths[first.Id], paths[second.Id], paths[skipped]]);
        second.MetadataStarted = _ => snapshots.Add([paths[first.Id], paths[second.Id], paths[skipped]]);
        var progress = new LibraryScanProgress(
            new InlineProgress(_ => { }),
            paths.Keys,
            started.Add,
            (path, percent) => paths[path] = Math.Max(paths[path], percent),
            path => paths[path] = 100);

        try
        {
            BaseItem.LibraryManager = library.Object;
            BaseItem.ItemRepository = repository.Object;
            BaseItem.ProviderManager = Mock.Of<IProviderManager>();
            BaseItem.Logger = NullLogger<BaseItem>.Instance;
            BaseItem.MediaSourceManager = mediaSource.Object;
            Folder.LimitedConcurrencyLibraryScheduler = new InlineScheduler();
            SynchronizationContext.SetSynchronizationContext(new InlineSynchronizationContext());

            await root.ValidateChildren(progress, new MetadataRefreshOptions(directory.Object), cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal([skipped, first.Id, second.Id], started);
            Assert.Equal(1, first.ValidationCount);
            Assert.Equal(1, second.ValidationCount);
            Assert.Equal(1, first.MetadataCount);
            Assert.Equal(1, second.MetadataCount);
            Assert.Single(validationSnapshots);
            Assert.Equal([40.0, 0.0, 100.0], validationSnapshots[0]);
            Assert.Equal(3, snapshots.Count);
            Assert.Equal([50.0, 50.0, 100.0], snapshots[0]);
            Assert.Equal([60.0, 50.0, 100.0], snapshots[1]);
            Assert.Equal([100.0, 50.0, 100.0], snapshots[2]);
            Assert.Equal(100.0, paths.Values.Average());

            // A delayed validation report must not undo a completed metadata phase.
            first.ValidationProgress!.Report(40);
            Assert.Equal(100.0, paths[first.Id]);
        }
        finally
        {
            BaseItem.LibraryManager = previousLibrary;
            BaseItem.ItemRepository = previousRepository;
            BaseItem.ProviderManager = previousProvider;
            BaseItem.Logger = previousLogger;
            BaseItem.MediaSourceManager = previousMediaSource;
            Folder.LimitedConcurrencyLibraryScheduler = previousScheduler;
            SynchronizationContext.SetSynchronizationContext(previousSynchronizationContext);
        }
    }

    private sealed class InlineProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    private sealed class InlineSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => d(state);
    }

    private sealed class InlineScheduler : ILimitedConcurrencyLibraryScheduler
    {
        public async Task Enqueue<T>(T[] data, Func<T, IProgress<double>, Task> worker, IProgress<double> progress, CancellationToken cancellationToken)
        {
            foreach (var item in data)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await worker(item, new InlineProgress(progress.Report));
            }

            progress.Report(100);
        }
    }

    private sealed class ScanRootFolder(params BaseItem[] children) : Folder
    {
        public override string GetClientTypeName() => nameof(Folder);

        protected override IEnumerable<BaseItem> GetNonCachedChildren(IDirectoryService directoryService) => children;

        protected override IReadOnlyList<BaseItem> LoadChildren() => children;
    }

    private sealed class ScanPathFolder : Folder, IMetadataContainer
    {
        public override bool IsTopParent => true;

        public int ValidationCount { get; private set; }

        public int MetadataCount { get; private set; }

        public IProgress<double>? ValidationProgress { get; private set; }

        public Action<IProgress<double>>? ValidationReported { get; set; }

        public Action<IProgress<double>>? MetadataStarted { get; set; }

        public Action<IProgress<double>>? MetadataReported { get; set; }

        public override string GetClientTypeName() => nameof(Folder);

        public Task RefreshAllMetadata(MetadataRefreshOptions refreshOptions, IProgress<double> progress, CancellationToken cancellationToken)
        {
            MetadataCount++;
            MetadataStarted?.Invoke(progress);
            progress.Report(20);
            MetadataReported?.Invoke(progress);
            return Task.CompletedTask;
        }

        protected override Task ValidateChildrenInternal(IProgress<double> progress, bool recursive, bool refreshChildMetadata, bool allowRemoveRoot, MetadataRefreshOptions refreshOptions, IDirectoryService directoryService, CancellationToken cancellationToken)
        {
            ValidationCount++;
            ValidationProgress = progress;
            progress.Report(40);
            ValidationReported?.Invoke(progress);
            return Task.CompletedTask;
        }
    }
}
