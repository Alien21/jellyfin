using System;
using System.Collections.Generic;
using System.Linq;
using Emby.Server.Implementations.Dto;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
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
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Dto;

public class DtoServiceLibraryInfoTests
{
    private readonly Mock<ILibraryManager> _libraryManagerMock = new();
    private readonly Mock<IProviderManager> _providerManagerMock = new();
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
            _providerManagerMock.Object,
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

    [Theory]
    [InlineData("Queued", "Sequential", 0.0, 0, 3)]
    [InlineData("Active", "Sequential", 45.0, 2, 3)]
    [InlineData("Active", "Aggregate", 60.0, 2, 3)]
    [InlineData("Idle", "Aggregate", 100.0, 3, 3)]
    public void GetBaseItemDto_AdministratorLibraryIncludesTheInitialRefreshSnapshot(
        string status,
        string mode,
        double progress,
        int current,
        int total)
    {
        var user = BuildRefreshUser(administrator: true);
        var library = BuildLibrary("Films", user);
        var operation = Guid.NewGuid();
        _providerManagerMock.Setup(p => p.GetLibraryRefreshProgress(library.Id)).Returns(new LibraryRefreshProgress
        {
            OperationId = operation,
            Status = status,
            Mode = mode,
            Progress = progress,
            CurrentPath = current,
            TotalPaths = total
        });

        var dto = _dtoService.GetBaseItemDto(library, RefreshInfoOptions(), user);

        Assert.Equal(library.Id, dto.Id);
        Assert.Equal(library.Id.ToString("N"), dto.RefreshItemId);
        Assert.Equal(operation.ToString("N"), dto.RefreshOperationId);
        Assert.Equal(status, dto.RefreshStatus);
        Assert.Equal(mode, dto.RefreshMode);
        Assert.Equal<int?>(current, dto.RefreshCurrentPath);
        Assert.Equal<int?>(total, dto.RefreshTotalPaths);
        Assert.Equal(status == "Idle" ? null : (double?)progress, dto.RefreshProgress);
        _providerManagerMock.Verify(p => p.GetLibraryRefreshProgress(library.Id), Times.Once);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GetBaseItemDto_AdministratorKeepsTheRefreshAliasWhileNoOperationIsActive(bool userView)
    {
        var user = BuildRefreshUser(administrator: true);
        var library = BuildLibrary("Films", user);
        BaseItem item = userView
            ? new UserView { Id = Guid.NewGuid(), Name = "Films view", DisplayParentId = library.Id }
            : library;
        _libraryManagerMock.Setup(l => l.GetItemById(library.Id)).Returns(library);

        var dto = _dtoService.GetBaseItemDto(item, RefreshInfoOptions(), user);

        Assert.Equal(item.Id, dto.Id);
        Assert.Equal(library.Id.ToString("N"), dto.RefreshItemId);
        Assert.Null(dto.RefreshProgress);
        Assert.Null(dto.RefreshStatus);
        Assert.Null(dto.RefreshMode);
        Assert.Null(dto.RefreshCurrentPath);
        Assert.Null(dto.RefreshTotalPaths);
        Assert.Null(dto.RefreshOperationId);
        _providerManagerMock.Verify(p => p.GetLibraryRefreshProgress(library.Id), Times.Once);
    }

    [Fact]
    public void GetBaseItemDto_UserViewRefreshAliasPreservesItsNavigationIdentifier()
    {
        var user = BuildRefreshUser(administrator: true);
        var library = BuildLibrary("Films", user);
        var view = new UserView { Id = Guid.NewGuid(), Name = "Films view", DisplayParentId = library.Id };
        var operation = Guid.NewGuid();
        _libraryManagerMock.Setup(l => l.GetItemById(library.Id)).Returns(library);
        _providerManagerMock.Setup(p => p.GetLibraryRefreshProgress(library.Id)).Returns(new LibraryRefreshProgress
        {
            OperationId = operation, Status = "Active", Mode = "Sequential", Progress = 25, CurrentPath = 2, TotalPaths = 3
        });

        var dto = _dtoService.GetBaseItemDto(view, RefreshInfoOptions(), user);

        Assert.Equal(view.Id, dto.Id);
        Assert.NotEqual(library.Id, dto.Id);
        Assert.Equal(library.Id.ToString("N"), dto.RefreshItemId);
        Assert.Equal(operation.ToString("N"), dto.RefreshOperationId);
        Assert.Equal("Active", dto.RefreshStatus);
        Assert.Equal("Sequential", dto.RefreshMode);
        Assert.Equal<double?>(25, dto.RefreshProgress);
        Assert.Equal<int?>(2, dto.RefreshCurrentPath);
        Assert.Equal<int?>(3, dto.RefreshTotalPaths);
        _providerManagerMock.Verify(p => p.GetLibraryRefreshProgress(view.Id), Times.Never);
        _providerManagerMock.Verify(p => p.GetLibraryRefreshProgress(library.Id), Times.Once);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GetBaseItemDto_NonAdministratorDoesNotQueryOrExposeRefreshState(bool userView)
    {
        var user = BuildRefreshUser(administrator: false);
        var library = BuildLibrary("Films", user);
        BaseItem item = userView
            ? new UserView { Id = Guid.NewGuid(), Name = "Films view", DisplayParentId = library.Id }
            : library;
        _libraryManagerMock.Setup(l => l.GetItemById(library.Id)).Returns(library);
        _providerManagerMock.Setup(p => p.GetLibraryRefreshProgress(library.Id)).Returns(new LibraryRefreshProgress
        {
            OperationId = Guid.NewGuid(), Status = "Queued", Mode = "Sequential", TotalPaths = 3
        });

        var dto = _dtoService.GetBaseItemDto(item, RefreshInfoOptions(), user);

        Assert.Equal(item.Id, dto.Id);
        AssertNoRefreshFields(dto);
        _providerManagerMock.Verify(p => p.GetLibraryRefreshProgress(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public void GetBaseItemDto_OrdinaryAdministratorResultsDoNotQueryLibraryRefreshState()
    {
        var user = BuildRefreshUser(administrator: true);
        var movie = BuildMovie(BuildLibrary("Films", user));

        var dto = _dtoService.GetBaseItemDto(movie, RefreshInfoOptions(), user);

        Assert.Equal(movie.Id, dto.Id);
        AssertNoRefreshFields(dto);
        _providerManagerMock.Verify(p => p.GetLibraryRefreshProgress(It.IsAny<Guid>()), Times.Never);
    }

    private static User BuildRefreshUser(bool administrator)
    {
        var user = new User("user", "auth-provider", "reset-provider") { Id = Guid.NewGuid() };
        user.SetPermission(PermissionKind.IsAdministrator, administrator);
        return user;
    }

    private static DtoOptions RefreshInfoOptions() => new(false)
    {
        EnableImages = false,
        EnableUserData = false
    };

    private static void AssertNoRefreshFields(BaseItemDto dto)
    {
        Assert.Null(dto.RefreshItemId);
        Assert.Null(dto.RefreshProgress);
        Assert.Null(dto.RefreshStatus);
        Assert.Null(dto.RefreshMode);
        Assert.Null(dto.RefreshCurrentPath);
        Assert.Null(dto.RefreshTotalPaths);
        Assert.Null(dto.RefreshOperationId);
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
        library.Setup(folder => folder.GetClientTypeName()).Returns(nameof(CollectionFolder));
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
