using System;
using Jellyfin.Api.Controllers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Jellyfin.Api.Tests.Controllers;

public class ItemRefreshControllerTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void RefreshItem_ImageChoices_AreQueuedIndependently(bool replaceImages, bool trickplay)
    {
        var item = new Movie { Id = Guid.NewGuid() };
        var library = new Mock<ILibraryManager>();
        library.Setup(manager => manager.GetItemById<BaseItem>(item.Id, It.IsAny<Guid>())).Returns(item);
        var providers = new Mock<IProviderManager>();
        var subject = new ItemRefreshController(library.Object, providers.Object, Mock.Of<IFileSystem>())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var response = subject.RefreshItem(
            item.Id,
            metadataRefreshMode: MetadataRefreshMode.None,
            imageRefreshMode: replaceImages ? MetadataRefreshMode.FullRefresh : MetadataRefreshMode.None,
            replaceAllImages: replaceImages,
            regenerateTrickplay: trickplay,
            ignoreNfo: true);

        Assert.IsType<NoContentResult>(response);
        providers.Verify(
            manager => manager.QueueRefresh(
                item.Id,
                It.Is<MetadataRefreshOptions>(options =>
                    options.MetadataRefreshMode == MetadataRefreshMode.None
                    && options.ImageRefreshMode == (replaceImages ? MetadataRefreshMode.FullRefresh : MetadataRefreshMode.None)
                    && options.ReplaceAllImages == replaceImages
                    && options.RegenerateTrickplay == trickplay
                    && !options.ReplaceAllMetadata
                    && !options.RemoveOldMetadata
                    && options.IgnoreNfo),
                RefreshPriority.High),
            Times.Once);
    }

    [Fact]
    public void RefreshItem_DefaultRequest_PreservesNfoReading()
    {
        var item = new Movie { Id = Guid.NewGuid() };
        var library = new Mock<ILibraryManager>();
        library.Setup(manager => manager.GetItemById<BaseItem>(item.Id, It.IsAny<Guid>())).Returns(item);
        var providers = new Mock<IProviderManager>();
        var subject = new ItemRefreshController(library.Object, providers.Object, Mock.Of<IFileSystem>())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        subject.RefreshItem(item.Id);

        providers.Verify(
            manager => manager.QueueRefresh(
                item.Id,
                It.Is<MetadataRefreshOptions>(options => !options.IgnoreNfo),
                RefreshPriority.High),
            Times.Once);
    }

    [Fact]
    public void RefreshItem_LibraryQueuesTheResolvedLibraryPlan()
    {
        var library = new CollectionFolder { Id = Guid.NewGuid() };
        var libraries = new Mock<ILibraryManager>();
        libraries.Setup(manager => manager.GetItemById<BaseItem>(library.Id, It.IsAny<Guid>())).Returns(library);
        var providers = new Mock<IProviderManager>();
        var subject = new ItemRefreshController(libraries.Object, providers.Object, Mock.Of<IFileSystem>())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        subject.RefreshItem(library.Id);

        providers.Verify(manager => manager.QueueRefresh(library, It.IsAny<MetadataRefreshOptions>(), RefreshPriority.High), Times.Once);
        providers.Verify(manager => manager.QueueRefresh(It.IsAny<Guid>(), It.IsAny<MetadataRefreshOptions>(), It.IsAny<RefreshPriority>()), Times.Never);
    }

    [Fact]
    public void RefreshOptions_Copy_PreservesNfoAndImageChoices()
    {
        var original = new MetadataRefreshOptions(Mock.Of<IDirectoryService>())
        {
            IgnoreNfo = true,
            MetadataRefreshMode = MetadataRefreshMode.None,
            ImageRefreshMode = MetadataRefreshMode.FullRefresh,
            ReplaceAllImages = true,
            RegenerateTrickplay = true
        };

        var copy = new MetadataRefreshOptions(original);

        Assert.True(copy.IgnoreNfo);
        Assert.True(copy.ReplaceAllImages);
        Assert.True(copy.RegenerateTrickplay);
        Assert.Equal(MetadataRefreshMode.None, copy.MetadataRefreshMode);
        Assert.Equal(MetadataRefreshMode.FullRefresh, copy.ImageRefreshMode);
    }
}
