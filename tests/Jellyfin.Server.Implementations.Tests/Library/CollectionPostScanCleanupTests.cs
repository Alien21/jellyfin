using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.Library.Validators;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Library;

public class CollectionPostScanCleanupTests
{
    private const string CollectionName = "Movie collection";

    [Fact]
    public async Task Run_OneMovieWithCopiesInMetadataCollection_RemovesOnlyCollection()
    {
        Movie[] movies = [Movie("100"), Movie("100"), Movie("100")];
        var folder = CollectionsFolder();
        folder.Path += Path.DirectorySeparatorChar;
        var boxSet = Collection(folder, movies);
        boxSet.Path = Path.Combine(folder.Path, "unused", "..", "Movie collection [boxset]");
        var task = CreateTask(movies, boxSet, folder, out var libraryManager, out var collectionManager);

        await task.Run(Mock.Of<IProgress<double>>(), CancellationToken.None).ConfigureAwait(true);

        libraryManager.Verify(
            x => x.DeleteItem(boxSet, It.Is<DeleteOptions>(options => options.DeleteFileLocation && !options.DeleteFromExternalProvider)),
            Times.Once);
        libraryManager.Verify(x => x.DeleteItem(It.Is<BaseItem>(item => !item.Id.Equals(boxSet.Id)), It.IsAny<DeleteOptions>()), Times.Never);
        collectionManager.Verify(x => x.GetCollectionsFolder(false), Times.Once);
        collectionManager.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Run_CollectionHasUnscannedOrUnresolvedChild_PreservesCollection(bool unresolvedChild)
    {
        Movie[] movies = [Movie("100"), Movie("100")];
        var folder = CollectionsFolder();
        var boxSet = Collection(folder, movies);
        boxSet.LinkedChildren = [.. boxSet.LinkedChildren, unresolvedChild ? new LinkedChild() : LinkedChild.Create(Movie("200"))];

        await AssertCollectionRetained(movies, boxSet, folder).ConfigureAwait(true);
    }

    [Fact]
    public async Task Run_CollectionPathOutsideMetadataFolder_PreservesCollection()
    {
        Movie[] movies = [Movie("100"), Movie("100")];
        var folder = CollectionsFolder();
        var boxSet = Collection(folder, movies);
        boxSet.Path = Path.Combine(Path.GetTempPath(), "media-library", "Movie collection [boxset]");

        await AssertCollectionRetained(movies, boxSet, folder).ConfigureAwait(true);
    }

    [Fact]
    public async Task Run_CollectionHasDifferentParent_PreservesCollection()
    {
        Movie[] movies = [Movie("100"), Movie("100")];
        var folder = CollectionsFolder();
        var boxSet = Collection(folder, movies);
        boxSet.ParentId = Guid.NewGuid();

        await AssertCollectionRetained(movies, boxSet, folder).ConfigureAwait(true);
    }

    [Fact]
    public async Task Run_CollectionWithoutLinkedChildren_PreservesCollection()
    {
        Movie[] movies = [Movie("100"), Movie("100")];
        var folder = CollectionsFolder();
        var boxSet = Collection(folder, movies);
        boxSet.LinkedChildren = [];

        await AssertCollectionRetained(movies, boxSet, folder).ConfigureAwait(true);
    }

    [Fact]
    public async Task Run_TwoDistinctMoviesWithCopies_PreservesEveryCopy()
    {
        Movie[] movies = [Movie("100"), Movie("100"), Movie("200"), Movie("200")];
        var folder = CollectionsFolder();
        var boxSet = Collection(folder, movies);

        await AssertCollectionRetained(movies, boxSet, folder, true).ConfigureAwait(true);
    }

    private static async Task AssertCollectionRetained(Movie[] movies, BoxSet boxSet, Folder folder, bool validCollection = false)
    {
        var task = CreateTask(movies, boxSet, folder, out var libraryManager, out var collectionManager);
        var expectedMovieIds = movies.Select(movie => movie.Id).ToHashSet();

        await task.Run(Mock.Of<IProgress<double>>(), CancellationToken.None).ConfigureAwait(true);

        libraryManager.Verify(x => x.DeleteItem(It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>()), Times.Never);
        collectionManager.Verify(
            x => x.AddToCollectionAsync(boxSet.Id, It.Is<IEnumerable<Guid>>(ids => expectedMovieIds.SetEquals(ids) && ids.Count() == expectedMovieIds.Count)),
            Times.Once);
        collectionManager.Verify(x => x.GetCollectionsFolder(false), validCollection ? Times.Never() : Times.AtMostOnce());
        collectionManager.VerifyNoOtherCalls();
    }

    private static CollectionPostScanTask CreateTask(
        Movie[] movies,
        BoxSet boxSet,
        Folder collectionsFolder,
        out Mock<ILibraryManager> libraryManager,
        out Mock<ICollectionManager> collectionManager)
    {
        var library = new Folder { Id = Guid.NewGuid() };
        var root = new Mock<AggregateFolder>();
        root.SetupGet(x => x.Children).Returns([library]);
        libraryManager = new Mock<ILibraryManager>(MockBehavior.Strict);
        libraryManager.SetupGet(x => x.RootFolder).Returns(root.Object);
        libraryManager.Setup(x => x.GetLibraryOptions(library))
            .Returns(new LibraryOptions { AutomaticallyAddToCollection = true });
        libraryManager.Setup(x => x.GetItemList(It.Is<InternalItemsQuery>(query => query.ParentId.Equals(library.Id))))
            .Returns(movies);
        libraryManager.Setup(x => x.GetItemList(It.Is<InternalItemsQuery>(query => query.IncludeItemTypes.Contains(BaseItemKind.BoxSet))))
            .Returns([boxSet]);
        libraryManager.Setup(x => x.DeleteItem(It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>()));
        collectionManager = new Mock<ICollectionManager>(MockBehavior.Strict);
        collectionManager.Setup(x => x.GetCollectionsFolder(false)).ReturnsAsync(collectionsFolder);
        collectionManager.Setup(x => x.AddToCollectionAsync(boxSet.Id, It.IsAny<IEnumerable<Guid>>()))
            .Returns(Task.CompletedTask);
        return new CollectionPostScanTask(libraryManager.Object, collectionManager.Object, NullLogger<CollectionPostScanTask>.Instance);
    }

    private static Folder CollectionsFolder()
        => new() { Id = Guid.NewGuid(), Path = Path.Combine(Path.GetTempPath(), "jellyfin-collections") };

    private static BoxSet Collection(Folder folder, Movie[] movies)
        => new()
        {
            Id = Guid.NewGuid(),
            Name = CollectionName,
            ParentId = folder.Id,
            Path = Path.Combine(folder.Path, "Movie collection [boxset]"),
            LinkedChildren = movies.Select(LinkedChild.Create).ToArray()
        };

    private static Movie Movie(string tmdb)
    {
        var movie = new Movie { Id = Guid.NewGuid(), Name = "Same title", CollectionName = CollectionName };
        movie.SetProviderId(MetadataProvider.Tmdb, tmdb);
        return movie;
    }
}
