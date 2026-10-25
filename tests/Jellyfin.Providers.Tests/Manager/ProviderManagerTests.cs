using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.BaseItemManager;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Lyrics;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Controller.Trickplay;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Providers;
using MediaBrowser.Model.Querying;
using MediaBrowser.Providers.Manager;
using MediaBrowser.Providers.Trickplay;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

// Allow Moq to see internal class
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]

namespace Jellyfin.Providers.Tests.Manager
{
    [Collection(nameof(ProviderManagerTests))]
    public class ProviderManagerTests
    {
        private static readonly ILogger<ProviderManager> _logger = new NullLogger<ProviderManager>();

        public static TheoryData<Mock<IMetadataService>[], int> RefreshSingleItemOrderData()
            => new()
            {
                // no order set, uses provided order
                {
                    new[]
                    {
                        MockIMetadataService(true, true),
                        MockIMetadataService(true, true)
                    },
                    0
                },
                // sort order sets priority when all match
                {
                    new[]
                    {
                        MockIMetadataService(true, true, 1),
                        MockIMetadataService(true, true, 0),
                        MockIMetadataService(true, true, 2)
                    },
                    1
                },
                // CanRefreshPrimary prioritized
                {
                    new[]
                    {
                        MockIMetadataService(false, true),
                        MockIMetadataService(true, true),
                    },
                    1
                },
                // falls back to CanRefresh
                {
                    new[]
                    {
                        MockIMetadataService(false, false),
                        MockIMetadataService(false, true)
                    },
                    1
                },
            };

        [Theory]
        [MemberData(nameof(RefreshSingleItemOrderData))]
        public async Task RefreshSingleItem_ServiceOrdering_FollowsPriority(Mock<IMetadataService>[] servicesList, int expectedIndex)
        {
            var item = new Movie();

            using var providerManager = GetProviderManager();
            AddParts(providerManager, metadataServices: servicesList.Select(s => s.Object).ToArray());

            var refreshOptions = new MetadataRefreshOptions(Mock.Of<IDirectoryService>(MockBehavior.Strict));
            var actual = await providerManager.RefreshSingleItem(item, refreshOptions, CancellationToken.None);

            Assert.Equal(ItemUpdateType.MetadataDownload, actual);
            for (var i = 0; i < servicesList.Length; i++)
            {
                var times = i == expectedIndex ? Times.Once() : Times.Never();
                servicesList[i].Verify(mock => mock.RefreshMetadata(It.IsAny<BaseItem>(), It.IsAny<MetadataRefreshOptions>(), It.IsAny<CancellationToken>()), times);
            }
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task RefreshSingleItem_RefreshMetadata_WhenServiceFound(bool serviceFound)
        {
            var item = new Movie();

            var servicesList = new[] { MockIMetadataService(false, serviceFound) };

            using var providerManager = GetProviderManager();
            AddParts(providerManager, metadataServices: servicesList.Select(s => s.Object).ToArray());

            var refreshOptions = new MetadataRefreshOptions(Mock.Of<IDirectoryService>(MockBehavior.Strict));
            var actual = await providerManager.RefreshSingleItem(item, refreshOptions, CancellationToken.None);

            var expectedResult = serviceFound ? ItemUpdateType.MetadataDownload : ItemUpdateType.None;
            Assert.Equal(expectedResult, actual);
        }

        [Fact]
        public async Task RefreshSingleItem_ArtworkExclusionsRemainLocalToEachItem()
        {
            var service = MockIMetadataService(true, true);
            var received = new List<MetadataRefreshOptions>();
            service.Setup(s => s.RefreshMetadata(It.IsAny<BaseItem>(), It.IsAny<MetadataRefreshOptions>(), It.IsAny<CancellationToken>()))
                .Callback<BaseItem, MetadataRefreshOptions, CancellationToken>((_, options, _) =>
                {
                    Assert.True(options.IgnoreNfo);
                    Assert.True(options.IsReplacingImage(ImageType.Primary));
                    received.Add(options);
                    new ItemImageProvider(_logger, Mock.Of<IProviderManager>(), Mock.Of<IFileSystem>())
                        .UpdateReplaceImages(options, [ImageType.Primary]);
                })
                .ReturnsAsync(ItemUpdateType.None);
            using var manager = GetProviderManager();
            AddParts(manager, metadataServices: [service.Object]);
            var original = new MetadataRefreshOptions(Mock.Of<IDirectoryService>())
            {
                MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                ImageRefreshMode = MetadataRefreshMode.FullRefresh,
                ReplaceAllImages = true,
                IgnoreNfo = true
            };

            await manager.RefreshSingleItem(new Movie(), original, CancellationToken.None);
            await manager.RefreshSingleItem(new Movie(), original, CancellationToken.None);

            Assert.True(original.ReplaceAllImages);
            Assert.NotSame(original, received[0]);
            Assert.NotSame(received[0], received[1]);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task RefreshSingleItem_ImagesOnly_ChangesArtworkWithoutMetadataServiceOrMetadataSavers(bool saveLocally)
        {
            var item = new ImageOnlyTestMovie
            {
                SaveLocally = saveLocally, Id = Guid.NewGuid(), Name = "User title", Overview = "User plot", PreferredMetadataLanguage = "cs",
                DateLastRefreshed = DateTime.UtcNow.AddDays(-5)
            };
            item.SetImage(new ItemImageInfo { Type = ImageType.Primary, Path = "https://example.org/old.jpg" }, 0);
            var stamp = item.DateLastRefreshed;
            var service = MockIMetadataService(true, true);
            var library = new Mock<ILibraryManager>();
            library.Setup(l => l.GetLibraryOptions(It.IsAny<BaseItem>())).Returns(new LibraryOptions());
            library.Setup(l => l.UpdateImagesAsync(item, true)).Returns(Task.CompletedTask);
            var baseItemManager = new Mock<IBaseItemManager>();
            baseItemManager.Setup(b => b.IsImageFetcherEnabled(It.IsAny<BaseItem>(), It.IsAny<TypeOptions>(), It.IsAny<string>())).Returns(true);
            var imageProvider = new Mock<IRemoteImageProvider>();
            imageProvider.Setup(p => p.Name).Returns("Images");
            imageProvider.Setup(p => p.Supports(item)).Returns(true);
            imageProvider.Setup(p => p.GetSupportedImages(item)).Returns([ImageType.Primary]);
            imageProvider.Setup(p => p.GetImages(item, It.IsAny<CancellationToken>()))
                .ReturnsAsync([new RemoteImageInfo { Type = ImageType.Primary, Language = "cs", Url = "https://example.org/czech.jpg" }]);
            var localImages = new Mock<ILocalImageProvider>();
            localImages.Setup(p => p.Name).Returns("Local");
            localImages.Setup(p => p.Supports(item)).Returns(true);
            if (saveLocally)
            {
                localImages.Setup(p => p.GetImages(item, It.IsAny<IDirectoryService>())).Throws(new InvalidOperationException("Replacement must skip local artwork import."));
            }
            else
            {
                localImages.Setup(p => p.GetImages(item, It.IsAny<IDirectoryService>())).Returns(Array.Empty<LocalImageInfo>());
            }

            using var manager = GetProviderManager(baseItemManager: baseItemManager.Object, libraryManager: library.Object);
            AddParts(manager, imageProviders: [localImages.Object, imageProvider.Object], metadataServices: [service.Object]);

            var changed = await manager.RefreshSingleItem(
                item,
                new MetadataRefreshOptions(Mock.Of<IDirectoryService>())
                {
                MetadataRefreshMode = MetadataRefreshMode.None,
                ImageRefreshMode = MetadataRefreshMode.FullRefresh,
                ReplaceAllImages = true
                },
                CancellationToken.None);

            localImages.Verify(p => p.GetImages(item, It.IsAny<IDirectoryService>()), saveLocally ? Times.Never() : Times.Once());
            Assert.True(changed.HasFlag(ItemUpdateType.ImageUpdate));
            Assert.Equal("https://example.org/czech.jpg", item.GetImagePath(ImageType.Primary, 0));
            Assert.Equal("User title", item.Name);
            Assert.Equal("User plot", item.Overview);
            Assert.Equal(stamp, item.DateLastRefreshed);
            service.Verify(s => s.RefreshMetadata(It.IsAny<BaseItem>(), It.IsAny<MetadataRefreshOptions>(), It.IsAny<CancellationToken>()), Times.Never);
            library.Verify(l => l.UpdateImagesAsync(item, true), Times.Once);
            library.Verify(l => l.RunMetadataSavers(It.IsAny<BaseItem>(), It.IsAny<ItemUpdateType>()), Times.Never);
        }

        [Fact]
        public async Task RefreshSingleItem_TrickplayOnly_DoesNotFetchRegularArtworkOrMetadata()
        {
            var item = new Movie { Name = "Unchanged" };
            var libraryOptions = new LibraryOptions { ExtractTrickplayImagesDuringLibraryScan = false };
            var library = new Mock<ILibraryManager>();
            library.Setup(l => l.GetLibraryOptions(item)).Returns(libraryOptions);
            var configuration = new ServerConfiguration();
            configuration.TrickplayOptions.ScanBehavior = TrickplayScanBehavior.Blocking;
            var configManager = Mock.Of<IServerConfigurationManager>(c => c.Configuration == configuration);
            var trickplayManager = new Mock<ITrickplayManager>();
            trickplayManager.Setup(t => t.RefreshTrickplayDataAsync(item, true, libraryOptions, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            var service = MockIMetadataService(true, true);
            var imageProvider = new Mock<IRemoteImageProvider>(MockBehavior.Strict);
            using var manager = GetProviderManager(serverConfiguration: configuration, libraryManager: library.Object);
            AddParts(
                manager,
                imageProviders: [imageProvider.Object],
                metadataServices: [service.Object],
                metadataProviders: [new TrickplayProvider(configManager, trickplayManager.Object, library.Object)]);

            await manager.RefreshSingleItem(
                item,
                new MetadataRefreshOptions(Mock.Of<IDirectoryService>())
                {
                MetadataRefreshMode = MetadataRefreshMode.None, ImageRefreshMode = MetadataRefreshMode.None, RegenerateTrickplay = true
                },
                CancellationToken.None);

            Assert.Equal("Unchanged", item.Name);
            trickplayManager.Verify(t => t.RefreshTrickplayDataAsync(item, true, libraryOptions, It.IsAny<CancellationToken>()), Times.Once);
            service.Verify(s => s.RefreshMetadata(It.IsAny<BaseItem>(), It.IsAny<MetadataRefreshOptions>(), It.IsAny<CancellationToken>()), Times.Never);
            imageProvider.VerifyNoOtherCalls();
            library.Verify(l => l.UpdateImagesAsync(It.IsAny<BaseItem>(), It.IsAny<bool>()), Times.Never);
        }

        [Fact]
        public async Task RefreshFullItem_ImagesOnly_RefreshesCollectionMembersWithoutCollapsingThem()
        {
            var member = new ImageOnlyTestMovie
            {
                Id = Guid.NewGuid(),
                Name = "Collection member",
                PreferredMetadataLanguage = "cs"
            };
            member.SetImage(new ItemImageInfo { Type = ImageType.Primary, Path = "https://example.org/english.jpg" }, 0);
            var collection = new BoxSet { Id = Guid.NewGuid(), Name = "Parent collection" };
            var folder = new CollectionTraversalTestFolder(member, collection)
            {
                Id = Guid.NewGuid(),
                Name = "Library"
            };
            var library = new Mock<ILibraryManager>();
            library.Setup(l => l.GetLibraryOptions(It.IsAny<BaseItem>())).Returns(new LibraryOptions());
            library.Setup(l => l.UpdateImagesAsync(member, true)).Returns(Task.CompletedTask);
            var baseItemManager = new Mock<IBaseItemManager>();
            baseItemManager.Setup(b => b.IsImageFetcherEnabled(It.IsAny<BaseItem>(), It.IsAny<TypeOptions>(), It.IsAny<string>())).Returns(true);
            var images = new Mock<IRemoteImageProvider>();
            images.Setup(p => p.Name).Returns("Images");
            images.Setup(p => p.Supports(It.IsAny<BaseItem>())).Returns<BaseItem>(item => ReferenceEquals(item, member));
            images.Setup(p => p.GetSupportedImages(member)).Returns([ImageType.Primary]);
            images.Setup(p => p.GetImages(member, It.IsAny<CancellationToken>()))
                .ReturnsAsync([new RemoteImageInfo { Type = ImageType.Primary, Language = "cs", Url = "https://example.org/czech.jpg" }]);
            using var manager = GetProviderManager(
                serverConfiguration: new ServerConfiguration { EnableGroupingMoviesIntoCollections = true },
                baseItemManager: baseItemManager.Object,
                libraryManager: library.Object);
            AddParts(manager, imageProviders: [images.Object]);

            await manager.RefreshFullItem(
                folder,
                new MetadataRefreshOptions(Mock.Of<IDirectoryService>())
                {
                    MetadataRefreshMode = MetadataRefreshMode.None,
                    ImageRefreshMode = MetadataRefreshMode.FullRefresh,
                    ReplaceAllImages = true
                },
                CancellationToken.None);

            Assert.False(folder.CollapseRequested);
            Assert.Equal("https://example.org/czech.jpg", member.GetImagePath(ImageType.Primary, 0));
            images.Verify(p => p.GetImages(member, It.IsAny<CancellationToken>()), Times.Once);
            images.Verify(p => p.GetImages(collection, It.IsAny<CancellationToken>()), Times.Never);
            library.Verify(l => l.UpdateImagesAsync(member, true), Times.Once);
            library.Verify(l => l.RunMetadataSavers(It.IsAny<BaseItem>(), It.IsAny<ItemUpdateType>()), Times.Never);
            Assert.Null(manager.GetRefreshProgress(folder.Id));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task RefreshFullItem_ImagesOnly_LibraryReportsPhysicalFolderProgressAndClearsIt(bool cancelAfterFirstChild)
        {
            var folder = new RefreshProgressTestFolder(new Movie { Id = Guid.NewGuid() }, new Movie { Id = Guid.NewGuid() })
            {
                Id = Guid.NewGuid()
            };
            var collection = new CollectionFolder { Id = Guid.NewGuid(), PhysicalFolderIds = [folder.Id] };
            var library = new Mock<ILibraryManager>();
            library.Setup(l => l.GetItemById(folder.Id)).Returns(folder);
            library.Setup(l => l.GetLibraryOptions(It.IsAny<BaseItem>())).Returns(new LibraryOptions());
            var service = MockIMetadataService(true, true);
            var directory = new Mock<IDirectoryService>(MockBehavior.Strict);
            using var manager = GetProviderManager(libraryManager: library.Object);
            AddParts(manager, metadataServices: [service.Object]);
            using var cancellation = new CancellationTokenSource();
            var started = new List<Guid>();
            var completed = new List<Guid>();
            var progress = new List<(Guid Id, double Value, double? LibraryValue)>();
            manager.RefreshStarted += (_, e) => started.Add(e.Argument.Id);
            manager.RefreshCompleted += (_, e) => completed.Add(e.Argument.Id);
            manager.RefreshProgress += (_, e) =>
            {
                progress.Add((e.Argument.Item1.Id, e.Argument.Item2, collection.GetRefreshProgress()));
                if (cancelAfterFirstChild)
                {
                    cancellation.Cancel();
                }
            };

            var previousLibraryManager = BaseItem.LibraryManager;
            var previousProviderManager = BaseItem.ProviderManager;
            BaseItem.LibraryManager = library.Object;
            BaseItem.ProviderManager = manager;
            try
            {
                var options = new MetadataRefreshOptions(directory.Object)
                {
                    MetadataRefreshMode = MetadataRefreshMode.None,
                    ImageRefreshMode = MetadataRefreshMode.FullRefresh,
                    ReplaceAllImages = true
                };
                if (cancelAfterFirstChild)
                {
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.RefreshFullItem(collection, options, cancellation.Token));
                }
                else
                {
                    await manager.RefreshFullItem(collection, options, cancellation.Token);
                }

                Assert.Equal(new[] { folder.Id }, started);
                Assert.Equal(new[] { folder.Id }, completed);
                Assert.Equal(cancelAfterFirstChild ? new[] { 50.0 } : new[] { 50.0, 100.0 }, progress.Select(p => p.Value));
                Assert.All(progress, p =>
                {
                    Assert.Equal(folder.Id, p.Id);
                    Assert.Equal(p.Value, p.LibraryValue);
                });
                Assert.Null(manager.GetRefreshProgress(folder.Id));
                Assert.Null(manager.GetRefreshProgress(collection.Id));
                Assert.Null(collection.GetRefreshProgress());
                Assert.NotNull(folder.LastQuery);
                Assert.False(folder.LastQuery.CollapseBoxSetItems);
                service.Verify(s => s.RefreshMetadata(It.IsAny<BaseItem>(), It.IsAny<MetadataRefreshOptions>(), It.IsAny<CancellationToken>()), Times.Never);
                library.Verify(l => l.RunMetadataSavers(It.IsAny<BaseItem>(), It.IsAny<ItemUpdateType>()), Times.Never);
                directory.VerifyNoOtherCalls();
            }
            finally
            {
                BaseItem.LibraryManager = previousLibraryManager;
                BaseItem.ProviderManager = previousProviderManager;
            }
        }

        public static TheoryData<int, int[]?, int[]?, int?[]?, int[]> GetImageProvidersOrderData()
            => new()
            {
                { 3, null, null, null, new[] { 0, 1, 2 } }, // no order options set

                // library options ordering
                { 3, Array.Empty<int>(), null, null, new[] { 0, 1, 2 } }, // no order provided
                { 3, new[] { 1 }, null, null, new[] { 1, 0, 2 } }, // one item in order
                { 3, new[] { 2, 1, 0 }, null, null, new[] { 2, 1, 0 } }, // full reverse order

                // server options ordering
                { 3, null, Array.Empty<int>(), null, new[] { 0, 1, 2 } }, // no order provided
                { 3, null, new[] { 1 }, null, new[] { 1, 0, 2 } }, // one item in order
                { 3, null, new[] { 2, 1, 0 }, null, new[] { 2, 1, 0 } }, // full reverse order

                // IHasOrder ordering
                { 3, null, null, new int?[] { null, 1, null }, new[] { 1, 0, 2 } }, // one item with defined order
                { 3, null, null, new int?[] { 2, 1, 0 }, new[] { 2, 1, 0 } }, // full reverse order

                // multiple orders set
                { 3, new[] { 1 }, new[] { 2, 0, 1 }, null, new[] { 1, 0, 2 } }, // partial library order first, server order ignored
                { 3, new[] { 1 }, null, new int?[] { 2, 0, 1 }, new[] { 1, 2, 0 } }, // library order first, then orderby
                { 3, new[] { 2, 1, 0 }, new[] { 1, 2, 0 }, new int?[] { 2, 0, 1 }, new[] { 2, 1, 0 } }, // library order wins
            };

        [Theory]
        [MemberData(nameof(GetImageProvidersOrderData))]
        public void GetImageProviders_ProviderOrder_MatchesExpected(int providerCount, int[]? libraryOrder, int[]? serverOrder, int?[]? hasOrderOrder, int[] expectedOrder)
        {
            var item = new Movie();

            var nameProvider = new Func<int, string>(i => "Provider" + i);

            var providerList = new List<IImageProvider>();
            for (var i = 0; i < providerCount; i++)
            {
                var order = hasOrderOrder?[i];
                providerList.Add(MockIImageProvider<ILocalImageProvider>(nameProvider(i), item, order: order));
            }

            var libraryOptions = CreateLibraryOptions(item.GetType().Name, imageFetcherOrder: libraryOrder?.Select(nameProvider).ToArray());
            var serverConfiguration = CreateServerConfiguration(item.GetType().Name, imageFetcherOrder: serverOrder?.Select(nameProvider).ToArray());

            using var providerManager = GetProviderManager(serverConfiguration: serverConfiguration, libraryOptions: libraryOptions);
            AddParts(providerManager, imageProviders: providerList);

            var refreshOptions = new ImageRefreshOptions(Mock.Of<IDirectoryService>(MockBehavior.Strict));
            var actualProviders = providerManager.GetImageProviders(item, refreshOptions).ToList();

            Assert.Equal(providerList.Count, actualProviders.Count);
            var actualOrder = actualProviders.Select(i => providerList.IndexOf(i)).ToArray();
            Assert.Equal(expectedOrder, actualOrder);
        }

        [Theory]
        [InlineData(true, false, true)]
        [InlineData(false, false, false)]
        [InlineData(true, true, false)]
        public void GetImageProviders_CanRefreshImagesBasic_WhenSupportsWithoutError(bool supports, bool errorOnSupported, bool expected)
        {
            GetImageProviders_CanRefreshImages_Tester(nameof(IImageProvider), supports, expected, errorOnSupported: errorOnSupported);
        }

        [Theory]
        [InlineData(nameof(ILocalImageProvider), false, true)]
        [InlineData(nameof(ILocalImageProvider), true, true)]
        [InlineData(nameof(IImageProvider), false, false)]
        [InlineData(nameof(IImageProvider), true, true)]
        public void GetImageProviders_CanRefreshImagesLocked_WhenLocalOrFullRefresh(string providerType, bool fullRefresh, bool expected)
        {
            GetImageProviders_CanRefreshImages_Tester(providerType, true, expected, itemLocked: true, fullRefresh: fullRefresh);
        }

        [Theory]
        [InlineData(nameof(ILocalImageProvider), false, true)]
        [InlineData(nameof(IRemoteImageProvider), true, true)]
        [InlineData(nameof(IDynamicImageProvider), true, true)]
        [InlineData(nameof(IRemoteImageProvider), false, false)]
        [InlineData(nameof(IDynamicImageProvider), false, false)]
        public void GetImageProviders_CanRefreshImagesBaseItemEnabled_WhenLocalOrEnabled(string providerType, bool enabled, bool expected)
        {
            GetImageProviders_CanRefreshImages_Tester(providerType, true, expected, baseItemEnabled: enabled);
        }

        private static void GetImageProviders_CanRefreshImages_Tester(
            string providerType,
            bool supports,
            bool expected,
            bool errorOnSupported = false,
            bool itemLocked = false,
            bool fullRefresh = false,
            bool baseItemEnabled = true)
        {
            var item = new Movie
            {
                IsLocked = itemLocked
            };

            var providerName = "provider";
            IImageProvider provider = providerType switch
            {
                "IImageProvider" => MockIImageProvider<IImageProvider>(providerName, item, supports: supports, errorOnSupported: errorOnSupported),
                "ILocalImageProvider" => MockIImageProvider<ILocalImageProvider>(providerName, item, supports: supports, errorOnSupported: errorOnSupported),
                "IRemoteImageProvider" => MockIImageProvider<IRemoteImageProvider>(providerName, item, supports: supports, errorOnSupported: errorOnSupported),
                "IDynamicImageProvider" => MockIImageProvider<IDynamicImageProvider>(providerName, item, supports: supports, errorOnSupported: errorOnSupported),
                _ => throw new ArgumentException("Unexpected provider type")
            };

            var refreshOptions = new ImageRefreshOptions(Mock.Of<IDirectoryService>(MockBehavior.Strict))
            {
                ImageRefreshMode = fullRefresh ? MetadataRefreshMode.FullRefresh : MetadataRefreshMode.Default
            };

            var baseItemManager = new Mock<IBaseItemManager>(MockBehavior.Strict);
            baseItemManager.Setup(i => i.IsImageFetcherEnabled(item, It.IsAny<TypeOptions>(), providerName))
                .Returns(baseItemEnabled);

            using var providerManager = GetProviderManager(baseItemManager: baseItemManager.Object);
            AddParts(providerManager, imageProviders: new[] { provider });

            var actualProviders = providerManager.GetImageProviders(item, refreshOptions).ToArray();

            Assert.Equal(expected ? 1 : 0, actualProviders.Length);
        }

        public static TheoryData<string[], int[]?, int[]?, int[]?, int[]?, int?[]?, int[]> GetMetadataProvidersOrderData()
        {
            var l = nameof(ILocalMetadataProvider);
            var r = nameof(IRemoteMetadataProvider);
            return new()
            {
                { new[] { l, l, r, r }, null, null, null, null, null, new[] { 0, 1, 2, 3 } }, // no order options set

                // library options ordering
                { new[] { l, l, r, r }, Array.Empty<int>(), Array.Empty<int>(), null, null, null, new[] { 0, 1, 2, 3 } }, // no order provided
                // local only
                { new[] { r, l, l, l }, new[] { 2 }, null, null, null, null, new[] { 2, 0, 1, 3 } }, // one item in order
                { new[] { r, l, l, l }, new[] { 3, 2, 1 }, null, null, null, null, new[] { 3, 2, 1, 0 } }, // full reverse order
                // remote only
                { new[] { l, r, r, r }, null, new[] { 2 }, null, null, null, new[] { 2, 0, 1, 3 } }, // one item in order
                { new[] { l, r, r, r }, null, new[] { 3, 2, 1 }, null, null, null, new[] { 3, 2, 1, 0 } }, // full reverse order
                // local and remote, note that results will be interleaved (odd but expected)
                { new[] { l, l, r, r }, new[] { 1 }, new[] { 3 }, null, null, null, new[] { 1, 3, 0, 2 } }, // one item in each order
                { new[] { l, l, l, r, r, r }, new[] { 2, 1, 0 }, new[] { 5, 4, 3 }, null, null, null, new[] { 2, 5, 1, 4, 0, 3 } }, // full reverse order

                // // server options ordering
                { new[] { l, l, r, r }, null, null, Array.Empty<int>(), Array.Empty<int>(), null, new[] { 0, 1, 2, 3 } }, // no order provided
                // local only
                { new[] { r, l, l, l }, null, null, new[] { 2 }, null, null, new[] { 2, 0, 1, 3 } }, // one item in order
                { new[] { r, l, l, l }, null, null, new[] { 3, 2, 1 }, null, null, new[] { 3, 2, 1, 0 } }, // full reverse order
                // remote only
                { new[] { l, r, r, r }, null, null, null, new[] { 2 }, null, new[] { 2, 0, 1, 3 } }, // one item in order
                { new[] { l, r, r, r }, null, null, null, new[] { 3, 2, 1 }, null, new[] { 3, 2, 1, 0 } }, // full reverse order
                // local and remote, note that results will be interleaved (odd but expected)
                { new[] { l, l, r, r }, null, null, new[] { 1 }, new[] { 3 }, null, new[] { 1, 3, 0, 2 } }, // one item in each order
                { new[] { l, l, l, r, r, r }, null, null, new[] { 2, 1, 0 }, new[] { 5, 4, 3 }, null, new[] { 2, 5, 1, 4, 0, 3 } }, // full reverse order

                // IHasOrder ordering (not interleaved, doesn't care about types)
                { new[] { l, l, r, r }, null, null, null, null, new int?[] { 2, null, 1, null }, new[] { 2, 0, 1, 3 } }, // partially defined
                { new[] { l, l, r, r }, null, null, null, null, new int?[] { 3, 2, 1, 0 }, new[] { 3, 2, 1, 0 } }, // full reverse order

                // multiple orders set
                { new[] { l, l, l, r, r, r }, new[] { 1 }, new[] { 4 }, new[] { 2, 1, 0 }, new[] { 5, 4, 3 }, null, new[] { 1, 4, 0, 2, 3, 5 } }, // partial library order first, server order ignored
                { new[] { l, l, l }, new[] { 1 }, null, null, null, new int?[] { 2, 0, 1 }, new[] { 1, 2, 0 } }, // library order first, then orderby
                { new[] { l, l, l, r, r, r }, new[] { 2, 1, 0 }, new[] { 5, 4, 3 }, new[] { 1, 2, 0 }, new[] { 4, 5, 3 }, new int?[] { 5, 4, 1, 6, 3, 2 }, new[] { 2, 5, 4, 1, 0, 3 } }, // library order wins (with orderby between local/remote)
            };
        }

        [Theory]
        [MemberData(nameof(GetMetadataProvidersOrderData))]
        public void GetMetadataProviders_ProviderOrder_MatchesExpected(
            string[] providers,
            int[]? libraryLocalOrder,
            int[]? libraryRemoteOrder,
            int[]? serverLocalOrder,
            int[]? serverRemoteOrder,
            int?[]? hasOrderOrder,
            int[] expectedOrder)
        {
            var item = new MetadataTestItem();

            var nameProvider = new Func<int, string>(i => "Provider" + i);

            var providerList = new List<IMetadataProvider<MetadataTestItem>>();
            for (var i = 0; i < providers.Length; i++)
            {
                var order = hasOrderOrder?[i];
                providerList.Add(MockIMetadataProviderMapper<MetadataTestItem, MetadataTestItemInfo>(providers[i], nameProvider(i), order: order));
            }

            var libraryOptions = CreateLibraryOptions(
                item.GetType().Name,
                localMetadataReaderOrder: libraryLocalOrder?.Select(nameProvider).ToArray(),
                metadataFetcherOrder: libraryRemoteOrder?.Select(nameProvider).ToArray());
            var serverConfiguration = CreateServerConfiguration(
                item.GetType().Name,
                localMetadataReaderOrder: serverLocalOrder?.Select(nameProvider).ToArray(),
                metadataFetcherOrder: serverRemoteOrder?.Select(nameProvider).ToArray());

            var baseItemManager = new Mock<IBaseItemManager>(MockBehavior.Strict);
            baseItemManager.Setup(i => i.IsMetadataFetcherEnabled(item, It.IsAny<TypeOptions>(), It.IsAny<string>()))
                .Returns(true);

            using var providerManager = GetProviderManager(serverConfiguration: serverConfiguration, baseItemManager: baseItemManager.Object);
            AddParts(providerManager, metadataProviders: providerList);

            var actualProviders = providerManager.GetMetadataProviders<MetadataTestItem>(item, libraryOptions).ToList();

            Assert.Equal(providerList.Count, actualProviders.Count);
            var actualOrder = actualProviders.Select(i => providerList.IndexOf(i)).ToArray();
            Assert.Equal(expectedOrder, actualOrder);
        }

        [Theory]
        [InlineData(nameof(IMetadataProvider))]
        [InlineData(nameof(ILocalMetadataProvider))]
        [InlineData(nameof(IRemoteMetadataProvider))]
        [InlineData(nameof(ICustomMetadataProvider))]
        public void GetMetadataProviders_CanRefreshMetadataBasic_ReturnsTrue(string providerType)
        {
            GetMetadataProviders_CanRefreshMetadata_Tester(providerType, true);
        }

        [Theory]
        [InlineData(nameof(ILocalMetadataProvider), false, true)]
        [InlineData(nameof(IRemoteMetadataProvider), false, false)]
        [InlineData(nameof(ICustomMetadataProvider), false, false)]
        [InlineData(nameof(ILocalMetadataProvider), true, true)]
        [InlineData(nameof(ICustomMetadataProvider), true, false)]
        public void GetMetadataProviders_CanRefreshMetadataLocked_WhenLocalOrForced(string providerType, bool forced, bool expected)
        {
            GetMetadataProviders_CanRefreshMetadata_Tester(providerType, expected, itemLocked: true, providerForced: forced);
        }

        [Theory]
        [InlineData(nameof(ILocalMetadataProvider), false, true)]
        [InlineData(nameof(ICustomMetadataProvider), false, true)]
        [InlineData(nameof(IRemoteMetadataProvider), false, false)]
        [InlineData(nameof(IRemoteMetadataProvider), true, true)]
        public void GetMetadataProviders_CanRefreshMetadataBaseItemEnabled_WhenEnabledOrNotRemote(string providerType, bool baseItemEnabled, bool expected)
        {
            GetMetadataProviders_CanRefreshMetadata_Tester(providerType, expected, baseItemEnabled: baseItemEnabled);
        }

        [Theory]
        [InlineData(nameof(IRemoteMetadataProvider), false, true)]
        [InlineData(nameof(ICustomMetadataProvider), false, true)]
        [InlineData(nameof(ILocalMetadataProvider), false, false)]
        [InlineData(nameof(ILocalMetadataProvider), true, true)]
        public void GetMetadataProviders_CanRefreshMetadataSupportsLocal_WhenSupportsOrNotLocal(string providerType, bool supportsLocalMetadata, bool expected)
        {
            GetMetadataProviders_CanRefreshMetadata_Tester(providerType, expected, supportsLocalMetadata: supportsLocalMetadata);
        }

        [Theory]
        [InlineData(nameof(ICustomMetadataProvider), true)]
        [InlineData(nameof(IRemoteMetadataProvider), true)]
        [InlineData(nameof(ILocalMetadataProvider), true)]
        public void GetMetadataProviders_CanRefreshMetadataOwned(string providerType, bool expected)
        {
            GetMetadataProviders_CanRefreshMetadata_Tester(providerType, expected, ownedItem: true);
        }

        [Fact]
        public async Task QueueRefresh_ManyItemsQueuedFromManyThreads_ProcessesEveryOne()
        {
            const int ItemCount = 2000;

            var queued = Enumerable.Range(0, ItemCount).Select(_ => Guid.NewGuid()).ToArray();
            var processed = new ConcurrentBag<Guid>();
            var allProcessed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var libraryManager = new Mock<ILibraryManager>();
            libraryManager.Setup(i => i.GetItemById(It.IsAny<Guid>()))
                .Returns((Guid id) =>
                {
                    // Returning null drains the entry without the whole refresh machinery.
                    processed.Add(id);
                    if (processed.Count == ItemCount)
                    {
                        allProcessed.TrySetResult();
                    }

                    return null;
                });

            using var providerManager = GetProviderManager(libraryManager: libraryManager.Object);

            await Parallel.ForEachAsync(
                queued,
                TestContext.Current.CancellationToken,
                (id, _) =>
                {
                    providerManager.QueueRefresh(id, new MetadataRefreshOptions(Mock.Of<IDirectoryService>()), RefreshPriority.Normal);
                    return ValueTask.CompletedTask;
                });

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));

            try
            {
                await allProcessed.Task.WaitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                // Fall through so the assertions report what was lost.
            }

            Assert.Empty(providerManager.GetRefreshQueue());
            Assert.Equal(queued.Order().ToArray(), processed.Order().ToArray());
        }

        [Fact]
        public async Task QueueRefresh_RefreshCancelsForItsOwnReasons_KeepsDrainingTheQueue()
        {
            // A provider timeout arrives as an OperationCanceledException, indistinguishable from
            // a shutdown; treating it as one would strand the rest of the queue.
            const int ItemCount = 200;

            var queued = Enumerable.Range(0, ItemCount).Select(_ => Guid.NewGuid()).ToArray();
            var processed = new ConcurrentBag<Guid>();
            var allProcessed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var allQueued = new ManualResetEventSlim(false);
            var cancelledOnce = false;

            var libraryManager = new Mock<ILibraryManager>();
            libraryManager.Setup(i => i.GetItemById(It.IsAny<Guid>()))
                .Returns((Guid id) =>
                {
                    if (!cancelledOnce)
                    {
                        cancelledOnce = true;

                        // Hold the first entry until the whole batch is queued.
                        allQueued.Wait(TimeSpan.FromSeconds(30));
                        throw new OperationCanceledException("provider timed out");
                    }

                    processed.Add(id);
                    if (processed.Count == ItemCount - 1)
                    {
                        allProcessed.TrySetResult();
                    }

                    return null;
                });

            using var providerManager = GetProviderManager(libraryManager: libraryManager.Object);

            foreach (var id in queued)
            {
                providerManager.QueueRefresh(id, new MetadataRefreshOptions(Mock.Of<IDirectoryService>()), RefreshPriority.Normal);
            }

            allQueued.Set();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));

            try
            {
                await allProcessed.Task.WaitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                // Fall through so the assertions report what was stranded.
            }

            Assert.Empty(providerManager.GetRefreshQueue());
            Assert.Equal(ItemCount - 1, processed.Count);
        }

        private static void GetMetadataProviders_CanRefreshMetadata_Tester(
            string providerType,
            bool expected,
            bool itemLocked = false,
            bool baseItemEnabled = true,
            bool providerForced = false,
            bool supportsLocalMetadata = true,
            bool ownedItem = false)
        {
            var item = new MetadataTestItem
            {
                IsLocked = itemLocked,
                OwnerId = ownedItem ? Guid.NewGuid() : Guid.Empty,
                EnableLocalMetadata = supportsLocalMetadata
            };

            var providerName = "provider";
            var provider = MockIMetadataProviderMapper<MetadataTestItem, MetadataTestItemInfo>(providerType, providerName, forced: providerForced);

            var baseItemManager = new Mock<IBaseItemManager>(MockBehavior.Strict);
            baseItemManager.Setup(i => i.IsMetadataFetcherEnabled(item, It.IsAny<TypeOptions>(), providerName))
                .Returns(baseItemEnabled);

            using var providerManager = GetProviderManager(baseItemManager: baseItemManager.Object);
            AddParts(providerManager, metadataProviders: new[] { provider });

            var actualProviders = providerManager.GetMetadataProviders<MetadataTestItem>(item, new LibraryOptions()).ToArray();

            Assert.Equal(expected ? 1 : 0, actualProviders.Length);
        }

        private static Mock<IMetadataService> MockIMetadataService(bool refreshPrimary, bool canRefresh, int order = 0)
        {
            var service = new Mock<IMetadataService>(MockBehavior.Strict);
            service.Setup(s => s.Order)
                .Returns(order);
            service.Setup(s => s.CanRefreshPrimary(It.IsAny<Type>()))
                .Returns(refreshPrimary);
            service.Setup(s => s.CanRefresh(It.IsAny<BaseItem>()))
                .Returns(canRefresh);
            service.Setup(s => s.RefreshMetadata(It.IsAny<BaseItem>(), It.IsAny<MetadataRefreshOptions>(), It.IsAny<CancellationToken>()))
                .Returns(Task.FromResult(ItemUpdateType.MetadataDownload));
            return service;
        }

        private static IImageProvider MockIImageProvider<TProviderType>(string name, BaseItem expectedType, bool supports = true, int? order = null, bool errorOnSupported = false)
            where TProviderType : class, IImageProvider
        {
            Mock<IHasOrder>? hasOrder = null;
            if (order is not null)
            {
                hasOrder = new Mock<IHasOrder>(MockBehavior.Strict);
                hasOrder.Setup(i => i.Order)
                    .Returns((int)order);
            }

            var provider = hasOrder is null
                ? new Mock<TProviderType>(MockBehavior.Strict)
                : hasOrder.As<TProviderType>();
            provider.Setup(p => p.Name)
                .Returns(name);
            if (errorOnSupported)
            {
                provider.Setup(p => p.Supports(It.IsAny<BaseItem>()))
                    .Throws(new ArgumentException("Provider threw exception on Supports(item)"));
            }
            else
            {
                provider.Setup(p => p.Supports(expectedType))
                    .Returns(supports);
            }

            return provider.Object;
        }

        private static IMetadataProvider<TItemType> MockIMetadataProviderMapper<TItemType, TLookupInfoType>(string typeName, string providerName, int? order = null, bool forced = false)
            where TItemType : BaseItem, IHasLookupInfo<TLookupInfoType>
            where TLookupInfoType : ItemLookupInfo, new()
            => typeName switch
            {
                "ILocalMetadataProvider" => MockIMetadataProvider<ILocalMetadataProvider<TItemType>, TItemType>(providerName, order, forced),
                "IRemoteMetadataProvider" => MockIMetadataProvider<IRemoteMetadataProvider<TItemType, TLookupInfoType>, TItemType>(providerName, order, forced),
                "ICustomMetadataProvider" => MockIMetadataProvider<ICustomMetadataProvider<TItemType>, TItemType>(providerName, order, forced),
                _ => MockIMetadataProvider<IMetadataProvider<TItemType>, TItemType>(providerName, order, forced)
            };

        private static IMetadataProvider<TItemType> MockIMetadataProvider<TProviderType, TItemType>(string name, int? order = null, bool forced = false)
            where TProviderType : class, IMetadataProvider<TItemType>
            where TItemType : BaseItem
        {
            Mock<IForcedProvider>? forcedProvider = null;
            if (forced)
            {
                forcedProvider = new Mock<IForcedProvider>();
            }

            Mock<IHasOrder>? hasOrder = null;
            if (order is not null)
            {
                hasOrder = forcedProvider is null ? new Mock<IHasOrder>() : forcedProvider.As<IHasOrder>();
                hasOrder.Setup(i => i.Order)
                    .Returns((int)order);
            }

            var provider = hasOrder is null
                ? new Mock<TProviderType>(MockBehavior.Strict)
                : hasOrder.As<TProviderType>();
            provider.Setup(p => p.Name)
                .Returns(name);

            return provider.Object;
        }

        private static LibraryOptions CreateLibraryOptions(
            string typeName,
            string[]? imageFetcherOrder = null,
            string[]? localMetadataReaderOrder = null,
            string[]? metadataFetcherOrder = null)
        {
            var libraryOptions = new LibraryOptions
            {
                LocalMetadataReaderOrder = localMetadataReaderOrder
            };

            // only create type options if populating it with something
            if (imageFetcherOrder is not null || metadataFetcherOrder is not null)
            {
                imageFetcherOrder ??= Array.Empty<string>();
                metadataFetcherOrder ??= Array.Empty<string>();

                libraryOptions.TypeOptions = new[]
                {
                    new TypeOptions
                    {
                        Type = typeName,
                        ImageFetcherOrder = imageFetcherOrder,
                        MetadataFetcherOrder = metadataFetcherOrder
                    }
                };
            }

            return libraryOptions;
        }

        private static ServerConfiguration CreateServerConfiguration(
            string typeName,
            string[]? imageFetcherOrder = null,
            string[]? localMetadataReaderOrder = null,
            string[]? metadataFetcherOrder = null)
        {
            var serverConfiguration = new ServerConfiguration();

            // only create type options if populating it with something
            if (imageFetcherOrder is not null || localMetadataReaderOrder is not null || metadataFetcherOrder is not null)
            {
                imageFetcherOrder ??= Array.Empty<string>();
                localMetadataReaderOrder ??= Array.Empty<string>();
                metadataFetcherOrder ??= Array.Empty<string>();

                serverConfiguration.MetadataOptions = new[]
                {
                    new MetadataOptions
                    {
                        ItemType = typeName,
                        ImageFetcherOrder = imageFetcherOrder,
                        LocalMetadataReaderOrder = localMetadataReaderOrder,
                        MetadataFetcherOrder = metadataFetcherOrder
                    }
                };
            }

            return serverConfiguration;
        }

        private static ProviderManager GetProviderManager(
            ServerConfiguration? serverConfiguration = null,
            LibraryOptions? libraryOptions = null,
            IBaseItemManager? baseItemManager = null,
            ILibraryManager? libraryManager = null)
        {
            var serverConfigurationManager = new Mock<IServerConfigurationManager>(MockBehavior.Strict);
            serverConfigurationManager.Setup(i => i.Configuration)
                .Returns(serverConfiguration ?? new ServerConfiguration());

            if (libraryManager is null)
            {
                var libraryManagerMock = new Mock<ILibraryManager>(MockBehavior.Strict);
                libraryManagerMock.Setup(i => i.GetLibraryOptions(It.IsAny<BaseItem>()))
                    .Returns(libraryOptions ?? new LibraryOptions());
                libraryManagerMock.Setup(i => i.GetCollectionFolders(It.IsAny<BaseItem>()))
                    .Returns(new List<Folder>());
                libraryManager = libraryManagerMock.Object;
            }

            var providerManager = new ProviderManager(
                Mock.Of<IHttpClientFactory>(),
                Mock.Of<ISubtitleManager>(),
                serverConfigurationManager.Object,
                Mock.Of<ILibraryMonitor>(),
                _logger,
                Mock.Of<IFileSystem>(),
                Mock.Of<IServerApplicationPaths>(),
                libraryManager,
                baseItemManager!,
                Mock.Of<ILyricManager>(),
                Mock.Of<IMemoryCache>(),
                Mock.Of<IMediaSegmentManager>(),
                Mock.Of<ISimilarItemsManager>());

            return providerManager;
        }

        private static void AddParts(
            ProviderManager providerManager,
            IEnumerable<IImageProvider>? imageProviders = null,
            IEnumerable<IMetadataService>? metadataServices = null,
            IEnumerable<IMetadataProvider>? metadataProviders = null,
            IEnumerable<IMetadataSaver>? metadataSavers = null,
            IEnumerable<IExternalId>? externalIds = null,
            IEnumerable<IExternalUrlProvider>? externalUrlProviders = null)
        {
            imageProviders ??= Array.Empty<IImageProvider>();
            metadataServices ??= Array.Empty<IMetadataService>();
            metadataProviders ??= Array.Empty<IMetadataProvider>();
            metadataSavers ??= Array.Empty<IMetadataSaver>();
            externalIds ??= Array.Empty<IExternalId>();
            externalUrlProviders ??= Array.Empty<IExternalUrlProvider>();

            providerManager.AddParts(imageProviders, metadataServices, metadataProviders, metadataSavers, externalIds, externalUrlProviders);
        }

        /// <summary>
        /// Simple <see cref="BaseItem"/> extension to make SupportsLocalMetadata directly settable.
        /// </summary>
        internal class MetadataTestItem : BaseItem, IHasLookupInfo<MetadataTestItemInfo>
        {
            public bool EnableLocalMetadata { get; set; } = true;

            public override bool SupportsLocalMetadata => EnableLocalMetadata;

            public MetadataTestItemInfo GetLookupInfo()
            {
                return GetItemLookupInfo<MetadataTestItemInfo>();
            }
        }

        private sealed class RefreshProgressTestFolder(params BaseItem[] children) : Folder
        {
            public InternalItemsQuery? LastQuery { get; private set; }

            public override bool IsSaveLocalMetadataEnabled() => false;

            protected override QueryResult<BaseItem> GetItemsInternal(InternalItemsQuery query)
            {
                LastQuery = query;
                return new QueryResult<BaseItem>(children);
            }
        }

        private sealed class CollectionTraversalTestFolder : Folder
        {
            private readonly BaseItem _member;
            private readonly BaseItem _collection;

            public CollectionTraversalTestFolder(BaseItem member, BaseItem collection)
            {
                _member = member;
                _collection = collection;
            }

            public bool? CollapseRequested { get; private set; }

            public override bool IsSaveLocalMetadataEnabled() => false;

            protected override QueryResult<BaseItem> GetItemsInternal(InternalItemsQuery query)
            {
                CollapseRequested = query.CollapseBoxSetItems;

                // Folder queries collapse collection members by default. The refresh must opt out
                // to receive the actual movie records rather than their parent collection.
                return new QueryResult<BaseItem>(
                    query.CollapseBoxSetItems == false ? new[] { _member } : new[] { _collection });
            }
        }

        private sealed class ImageOnlyTestMovie : Movie
        {
            public bool SaveLocally { get; set; }

            public override bool IsSaveLocalMetadataEnabled() => SaveLocally;
        }

        internal class MetadataTestItemInfo : ItemLookupInfo
        {
        }
    }
}
