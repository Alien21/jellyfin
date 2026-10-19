using System;
using System.Collections.Generic;
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

public class CollectionPostScanTaskTests
{
    private const string CollectionName = "Movie collection";

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Run_OneMovieInMultipleLibraries_DoesNotCreateCollection(int libraryCount)
    {
        var moviesByLibrary = Enumerable.Range(0, libraryCount)
            .Select(_ => new[] { Movie("100") })
            .ToArray();
        var task = CreateTask(moviesByLibrary, out var collectionManager);

        await task.Run(Mock.Of<IProgress<double>>(), CancellationToken.None).ConfigureAwait(true);

        collectionManager.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Run_OneMovieWithPartialProviderMetadata_DoesNotCreateCollection()
    {
        Movie[][] moviesByLibrary = [[Movie("100")], [Movie(null, "TT100")], [Movie("100", "tt100")]];
        var task = CreateTask(moviesByLibrary, out var collectionManager);

        await task.Run(Mock.Of<IProgress<double>>(), CancellationToken.None).ConfigureAwait(true);

        collectionManager.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Run_TwoMoviesInMultipleLibraries_CreatesCollectionWithEveryCopy(int libraryCount)
    {
        var moviesByLibrary = Enumerable.Range(0, libraryCount)
            .Select(_ => new[] { Movie("100"), Movie("200") })
            .ToArray();
        await VerifyCollectionContainsEveryCopy(moviesByLibrary).ConfigureAwait(true);
    }

    [Fact]
    public async Task Run_IdenticalTitlesWithDistinctProviderIds_CreatesCollection()
    {
        Movie[][] moviesByLibrary = [[Movie("100"), Movie("200")]];
        await VerifyCollectionContainsEveryCopy(moviesByLibrary).ConfigureAwait(true);
    }

    private static async Task VerifyCollectionContainsEveryCopy(Movie[][] moviesByLibrary)
    {
        var task = CreateTask(moviesByLibrary, out var collectionManager);
        var collectionId = Guid.NewGuid();
        var expectedMovieIds = moviesByLibrary.SelectMany(movies => movies).Select(movie => movie.Id).ToHashSet();
        collectionManager.Setup(x => x.CreateCollectionAsync(It.IsAny<CollectionCreationOptions>()))
            .ReturnsAsync(new BoxSet { Id = collectionId });
        collectionManager.Setup(x => x.AddToCollectionAsync(collectionId, It.IsAny<IEnumerable<Guid>>()))
            .Returns(Task.CompletedTask);

        await task.Run(Mock.Of<IProgress<double>>(), CancellationToken.None).ConfigureAwait(true);

        collectionManager.Verify(x => x.CreateCollectionAsync(It.Is<CollectionCreationOptions>(options => options.Name == CollectionName)), Times.Once);
        collectionManager.Verify(
            x => x.AddToCollectionAsync(collectionId, It.Is<IEnumerable<Guid>>(ids => expectedMovieIds.SetEquals(ids) && ids.Count() == expectedMovieIds.Count)),
            Times.Once);
        collectionManager.VerifyNoOtherCalls();
    }

    private static CollectionPostScanTask CreateTask(Movie[][] moviesByLibrary, out Mock<ICollectionManager> collectionManager)
    {
        var libraries = moviesByLibrary.Select(_ => new Folder { Id = Guid.NewGuid() }).ToArray();
        var root = new Mock<AggregateFolder>();
        root.SetupGet(x => x.Children).Returns(libraries);
        var libraryManager = new Mock<ILibraryManager>(MockBehavior.Strict);
        libraryManager.SetupGet(x => x.RootFolder).Returns(root.Object);
        libraryManager.Setup(x => x.GetLibraryOptions(It.IsAny<BaseItem>()))
            .Returns(new LibraryOptions { AutomaticallyAddToCollection = true });
        libraryManager.Setup(x => x.GetItemList(It.Is<InternalItemsQuery>(query => query.IncludeItemTypes.Contains(BaseItemKind.BoxSet))))
            .Returns(Array.Empty<BaseItem>());
        for (var i = 0; i < libraries.Length; i++)
        {
            var libraryId = libraries[i].Id;
            var movies = moviesByLibrary[i];
            libraryManager.Setup(x => x.GetItemList(It.Is<InternalItemsQuery>(query => query.ParentId.Equals(libraryId))))
                .Returns(movies);
        }

        collectionManager = new Mock<ICollectionManager>(MockBehavior.Strict);
        return new CollectionPostScanTask(libraryManager.Object, collectionManager.Object, NullLogger<CollectionPostScanTask>.Instance);
    }

    private static Movie Movie(string? tmdb = null, string? imdb = null)
    {
        var movie = new Movie { Id = Guid.NewGuid(), Name = "Same title", CollectionName = CollectionName };
        if (tmdb is not null)
        {
            movie.SetProviderId(MetadataProvider.Tmdb, tmdb);
        }

        if (imdb is not null)
        {
            movie.SetProviderId(MetadataProvider.Imdb, imdb);
        }

        return movie;
    }
}
