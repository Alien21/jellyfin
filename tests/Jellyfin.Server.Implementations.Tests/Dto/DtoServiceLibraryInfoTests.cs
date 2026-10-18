using System;
using System.Collections.Generic;
using System.Linq;
using Emby.Server.Implementations.Dto;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Common;
using MediaBrowser.Controller.Chapters;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Trickplay;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Dto;

public class DtoServiceLibraryInfoTests
{
    private readonly Mock<ILibraryManager> _libraryManagerMock = new();
    private readonly Dictionary<Guid, List<Folder>> _memberships = new();
    private readonly DtoService _dtoService;

    public DtoServiceLibraryInfoTests()
    {
        var appHost = new Mock<IApplicationHost>();
        appHost.Setup(x => x.SystemId).Returns("test-server");

        Video.RecordingsManager = new Mock<IRecordingsManager>().Object;

        _libraryManagerMock
            .Setup(x => x.GetCollectionFolders(It.IsAny<BaseItem>(), It.IsAny<IEnumerable<Folder>>()))
            .Returns<BaseItem, IEnumerable<Folder>>((item, candidates) =>
            {
                var candidateIds = candidates.Select(folder => folder.Id).ToHashSet();
                return _memberships.GetValueOrDefault(item.Id, [])
                    .Where(folder => candidateIds.Contains(folder.Id))
                    .ToList();
            });

        _dtoService = new DtoService(
            NullLogger<DtoService>.Instance,
            _libraryManagerMock.Object,
            new Mock<IUserDataManager>().Object,
            new Mock<IImageProcessor>().Object,
            new Mock<IProviderManager>().Object,
            new Mock<IRecordingsManager>().Object,
            appHost.Object,
            new Mock<IMediaSourceManager>().Object,
            new Lazy<ILiveTvManager>(() => new Mock<ILiveTvManager>().Object),
            new Mock<ITrickplayManager>().Object,
            new Mock<IChapterManager>().Object);
    }

    [Fact]
    public void GetBaseItemDtos_SameMovieTitleInDifferentLibraries_ReturnsEachLibraryAndLoadsRootOnce()
    {
        var libraryNames = new[] { "Filmy", "4K filmy", "3D filmy" };
        var libraries = libraryNames.Select(name => BuildLibrary(name)).ToArray();
        var movies = libraries.Select(library =>
        {
            var movie = BuildMovie(library);
            movie.TopParentId = library.PhysicalFolderIds[0];
            return movie;
        }).ToArray();
        SetRootChildren(libraries);

        var dtos = _dtoService.GetBaseItemDtos(movies, LibraryInfoOptions());

        Assert.Equal(3, dtos.Count);
        for (var index = 0; index < movies.Length; index++)
        {
            Assert.Equal("Vřískot 7", dtos[index].Name);
            Assert.Equal(libraries[index].Id, dtos[index].LibraryId);
            Assert.Equal(libraryNames[index], dtos[index].LibraryName);
        }

        _libraryManagerMock.Verify(x => x.GetUserRootFolder(), Times.Once);
        VerifyNoParentResolution();
    }

    [Fact]
    public void GetBaseItemDto_KnownPhysicalRoot_ReturnsLibraryWithoutResolvingParents()
    {
        var library = BuildLibrary("4K filmy");
        var movie = BuildMovie(library);
        movie.ParentId = Guid.NewGuid();
        movie.TopParentId = library.PhysicalFolderIds[0];
        SetRootChildren(library);

        var dto = _dtoService.GetBaseItemDto(movie, LibraryInfoOptions());

        Assert.Equal(library.Id, dto.LibraryId);
        Assert.Equal("4K filmy", dto.LibraryName);
        Assert.NotEqual(movie.ParentId, dto.LibraryId);
        VerifyNoParentResolution();
    }

    [Theory]
    [InlineData(BaseItemKind.Series, "Seriály")]
    [InlineData(BaseItemKind.Episode, "Seriály")]
    [InlineData(BaseItemKind.Photo, "Fotografie")]
    public void GetBaseItemDtos_NonMovieResultKnownPhysicalRoot_ReturnsLibraryWithoutResolvingParents(BaseItemKind kind, string libraryName)
    {
        var user = new User("user", "auth-provider", "reset-provider");
        var library = BuildLibrary(libraryName, user);
        BaseItem item = kind switch
        {
            BaseItemKind.Series => new Series(),
            BaseItemKind.Episode => new Episode(),
            BaseItemKind.Photo => new Photo(),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        item.Id = Guid.NewGuid();
        item.Name = "Search result";
        item.TopParentId = library.PhysicalFolderIds[0];
        SetRootChildren(library);

        var dto = _dtoService.GetBaseItemDtos([item], LibraryInfoOptions(), user, skipVisibilityCheck: true)[0];

        Assert.Equal(kind, dto.Type);
        Assert.Equal(library.Id, dto.LibraryId);
        Assert.Equal(libraryName, dto.LibraryName);
        _libraryManagerMock.Verify(x => x.GetUserRootFolder(), Times.Once);
        VerifyNoParentResolution();
    }

    [Fact]
    public void GetBaseItemDto_MovieInNestedFolder_ReturnsLibraryInsteadOfImmediateParent()
    {
        var library = BuildLibrary("4K filmy");
        var movie = BuildMovie(library);
        movie.ParentId = Guid.NewGuid();
        SetRootChildren(library);

        var dto = _dtoService.GetBaseItemDto(movie, LibraryInfoOptions());

        Assert.NotEqual(movie.ParentId, dto.LibraryId);
        Assert.Equal(library.Id, dto.LibraryId);
        Assert.Equal("4K filmy", dto.LibraryName);
        _libraryManagerMock.Verify(x => x.GetUserRootFolder(), Times.Once);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GetBaseItemDto_NoMatchingLibrary_LeavesLibraryInfoNull(bool hasLibrary)
    {
        SetRootChildren(hasLibrary ? [BuildLibrary("Filmy")] : []);
        var movie = new Movie { Id = Guid.NewGuid(), Name = "Outside the libraries" };

        var dto = _dtoService.GetBaseItemDto(movie, LibraryInfoOptions());

        Assert.Null(dto.LibraryId);
        Assert.Null(dto.LibraryName);
    }

    [Fact]
    public void GetBaseItemDtos_HiddenLibraryBeforeVisibleLibrary_ReturnsVisibleLibrary()
    {
        var user = new User("user", "auth-provider", "reset-provider");
        var hidden = BuildLibrary("Hidden films", user, visible: false);
        var visible = BuildLibrary("Filmy", user);
        var movie = BuildMovie(hidden, visible);
        SetRootChildren(hidden, visible);

        var dto = _dtoService.GetBaseItemDtos([movie], LibraryInfoOptions(), user, skipVisibilityCheck: true)[0];

        Assert.Equal(visible.Id, dto.LibraryId);
        Assert.Equal("Filmy", dto.LibraryName);
    }

    [Fact]
    public void GetBaseItemDtos_OnlyHiddenLibrary_LeavesLibraryInfoNull()
    {
        var user = new User("user", "auth-provider", "reset-provider");
        var hidden = BuildLibrary("Hidden films", user, visible: false);
        var movie = BuildMovie(hidden);
        SetRootChildren(hidden);

        var dto = _dtoService.GetBaseItemDtos([movie], LibraryInfoOptions(), user, skipVisibilityCheck: true)[0];

        Assert.Null(dto.LibraryId);
        Assert.Null(dto.LibraryName);
    }

    [Fact]
    public void GetBaseItemDto_OrdinaryFolderAtRoot_DoesNotReturnItAsLibrary()
    {
        var folder = new Folder { Id = Guid.NewGuid(), Name = "Ordinary folder" };
        var movie = BuildMovie(folder);
        SetRootChildren(folder);

        var dto = _dtoService.GetBaseItemDto(movie, LibraryInfoOptions());

        Assert.Null(dto.LibraryId);
        Assert.Null(dto.LibraryName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GetBaseItemDto_LibraryInfoNotRequested_DoesNotResolveLibraries(bool batch)
    {
        var library = BuildLibrary("4K filmy");
        var movie = BuildMovie(library);
        SetRootChildren(library);
        var options = new DtoOptions(false) { EnableImages = false, EnableUserData = false };

        var dto = batch
            ? _dtoService.GetBaseItemDtos([movie], options)[0]
            : _dtoService.GetBaseItemDto(movie, options);

        Assert.Null(dto.LibraryId);
        Assert.Null(dto.LibraryName);
        _libraryManagerMock.Verify(x => x.GetUserRootFolder(), Times.Never);
        VerifyNoParentResolution();
    }

    [Fact]
    public void DtoOptions_DefaultFields_RequireLibraryInfoToBeRequestedExplicitly()
    {
        Assert.False(new DtoOptions().ContainsField(ItemFields.LibraryInfo));
        Assert.True(LibraryInfoOptions().ContainsField(ItemFields.LibraryInfo));
    }

    private static DtoOptions LibraryInfoOptions() => new(false)
    {
        EnableImages = false,
        EnableUserData = false,
        Fields = [ItemFields.LibraryInfo]
    };

    private static CollectionFolder BuildLibrary(string name, User? user = null, bool visible = true)
    {
        var library = new Mock<CollectionFolder>();
        library.Object.Id = Guid.NewGuid();
        library.SetupProperty(folder => folder.Name, name);
        library.Object.PhysicalFolderIds = [Guid.NewGuid()];
        if (user is not null)
        {
            library.Setup(folder => folder.IsVisible(user, It.IsAny<bool>())).Returns(visible);
        }

        return library.Object;
    }

    private Movie BuildMovie(params Folder[] libraries)
    {
        var movie = new Movie { Id = Guid.NewGuid(), Name = "Vřískot 7" };
        _memberships.Add(movie.Id, libraries.ToList());
        return movie;
    }

    private void SetRootChildren(params BaseItem[] children)
    {
        var root = new Folder { Id = Guid.NewGuid(), Children = children };
        _libraryManagerMock.Setup(manager => manager.GetUserRootFolder()).Returns(root);
    }

    private void VerifyNoParentResolution()
    {
        _libraryManagerMock.Verify(x => x.GetCollectionFolders(It.IsAny<BaseItem>()), Times.Never);
        _libraryManagerMock.Verify(
            x => x.GetCollectionFolders(It.IsAny<BaseItem>(), It.IsAny<IEnumerable<Folder>>()),
            Times.Never);
    }
}
