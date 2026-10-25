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
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Library;

public sealed class CollectionPostScanIdentityTests
{
    private const string EnglishName = "Dirty Harry Collection";
    private const string CzechName = "Drsný Harry (kolekce)";

    [Theory]
    [InlineData("10456", "10456")]
    [InlineData("0010456", "10456")]
    [InlineData("10456", "0010456")]
    [InlineData(" 10456 ", "10456")]
    public async Task Run_LocalizedExistingCollectionKeepsItsIdentityAndName(string movieCollectionId, string boxSetId)
    {
        Movie[] movies = [Movie("100", EnglishName, movieCollectionId), Movie("200", EnglishName, movieCollectionId)];
        var existing = Collection(CzechName, boxSetId);
        var originalId = existing.Id;
        var scenario = new Scenario([movies], [existing]);

        await scenario.Run().ConfigureAwait(true);

        Assert.Empty(scenario.Created);
        AssertAdded(scenario, originalId, movies);
        Assert.Equal(originalId, existing.Id);
        Assert.Equal(CzechName, existing.Name);
        Assert.Equal(boxSetId, existing.GetProviderId(MetadataProvider.Tmdb));
    }

    [Fact]
    public async Task Run_DifferentLocalizedNamesForOneIdCreateOneCollectionWithEveryMovie()
    {
        Movie[][] libraries =
        [
            [Movie("100", EnglishName, "10456"), Movie("200", CzechName, "10456")],
            [Movie("100", CzechName, "10456"), Movie("300", "ダーティハリー コレクション", "10456")]
        ];
        var scenario = new Scenario(libraries);

        await scenario.Run().ConfigureAwait(true);

        var created = Assert.Single(scenario.Created);
        Assert.Equal("10456", created.GetProviderId(MetadataProvider.Tmdb));
        AssertAdded(scenario, created.Id, libraries.SelectMany(movies => movies));
    }

    [Fact]
    public async Task Run_SameNameWithConflictingCollectionIdDoesNotMergeOrReuseItsPhysicalFolder()
    {
        Movie[] movies = [Movie("100", EnglishName, "10456"), Movie("200", EnglishName, "10456")];
        var existing = Collection(EnglishName, "99999");
        var scenario = new Scenario([movies], [existing]);

        await scenario.Run().ConfigureAwait(true);

        Assert.Empty(scenario.Created);
        Assert.Empty(scenario.Additions);
        Assert.Equal("99999", existing.GetProviderId(MetadataProvider.Tmdb));
        Assert.Equal(EnglishName, existing.Name);
    }

    [Fact]
    public async Task Run_NewConflictingIdsWithTheSameNameDoNotCreateCollectionsOnOnePath()
    {
        Movie[] movies =
        [
            Movie("100", EnglishName, "1"), Movie("200", EnglishName, "1"),
            Movie("300", EnglishName, "2"), Movie("400", EnglishName, "2")
        ];
        var scenario = new Scenario([movies]);

        await scenario.Run().ConfigureAwait(true);

        Assert.Empty(scenario.Created);
        Assert.Empty(scenario.Additions);
    }

    [Fact]
    public async Task Run_LegacyMovieWithOneKnownNameAliasJoinsTheIdGroup()
    {
        Movie[] movies =
        [
            Movie("100", EnglishName, "10456"), Movie("200", CzechName, "10456"), Movie("300", EnglishName)
        ];
        var existing = Collection(CzechName, "10456");
        var scenario = new Scenario([movies], [existing]);

        await scenario.Run().ConfigureAwait(true);

        Assert.Empty(scenario.Created);
        AssertAdded(scenario, existing.Id, movies);
    }

    [Fact]
    public async Task Run_LegacyMovieWithAmbiguousNameDoesNotJoinEitherKnownId()
    {
        var first = Movie("100", "Shared collection name", "1");
        var second = Movie("200", "Shared collection name", "2");
        var legacy = Movie("300", "Shared collection name");
        var firstCollection = Collection("First localized collection", "1");
        var secondCollection = Collection("Second localized collection", "2");
        var scenario = new Scenario([[first, second, legacy]], [firstCollection, secondCollection]);

        await scenario.Run().ConfigureAwait(true);

        Assert.Empty(scenario.Created);
        Assert.Equal(2, scenario.Additions.Count);
        AssertAdded(scenario, firstCollection.Id, [first]);
        AssertAdded(scenario, secondCollection.Id, [second]);
        Assert.DoesNotContain(scenario.Additions, addition => addition.MovieIds.Contains(legacy.Id));
    }

    [Fact]
    public async Task Run_NameOnlyLegacyGroupReusesAnUnidentifiedCollection()
    {
        Movie[] movies = [Movie("100", EnglishName), Movie("200", EnglishName)];
        var existing = Collection(EnglishName);
        var scenario = new Scenario([movies], [existing]);

        await scenario.Run().ConfigureAwait(true);

        Assert.Empty(scenario.Created);
        AssertAdded(scenario, existing.Id, movies);
        Assert.Equal(EnglishName, existing.Name);
    }

    [Fact]
    public async Task Run_KnownIdReusesAnUnidentifiedCollectionWithTheExactName()
    {
        Movie[] movies = [Movie("100", EnglishName, "10456"), Movie("200", EnglishName, "10456")];
        var existing = Collection(EnglishName);
        var scenario = new Scenario([movies], [existing]);

        await scenario.Run().ConfigureAwait(true);

        Assert.Empty(scenario.Created);
        AssertAdded(scenario, existing.Id, movies);
        Assert.Equal(EnglishName, existing.Name);
    }

    [Fact]
    public async Task Run_NameOnlyLegacyGroupAlreadyLinkedToARenamedCollectionDoesNotCreateADuplicate()
    {
        Movie[] movies = [Movie("100", EnglishName), Movie("200", EnglishName)];
        var existing = Collection(CzechName, "10456");
        existing.LinkedChildren = [LinkedChild.Create(movies[0])];
        var scenario = new Scenario([movies], [existing]);

        await scenario.Run().ConfigureAwait(true);

        Assert.Empty(scenario.Created);
        Assert.Empty(scenario.Additions);
        Assert.Equal(CzechName, existing.Name);
        Assert.Equal(movies[0].Id, Assert.Single(existing.LinkedChildren).ItemId);
    }

    [Fact]
    public async Task Run_KnownIdDoesNotAdoptLegacyCollectionWithAnUnscannedLinkedChild()
    {
        Movie[] movies = [Movie("100", EnglishName, "10456"), Movie("200", EnglishName, "10456")];
        var unscanned = Movie("300", "Other collection", "99999");
        var existing = Collection(EnglishName);
        existing.LinkedChildren = [LinkedChild.Create(unscanned)];
        var scenario = new Scenario([movies], [existing]);

        await scenario.Run().ConfigureAwait(true);

        Assert.Empty(scenario.Created);
        Assert.Empty(scenario.Additions);
        Assert.False(existing.TryGetProviderId(MetadataProvider.Tmdb, out _));
        Assert.Equal(EnglishName, existing.Name);
        Assert.Equal(unscanned.Id, Assert.Single(existing.LinkedChildren).ItemId);
    }

    [Fact]
    public async Task Run_IdWithoutAnyNameHintStillAddsMoviesToTheExistingCollection()
    {
        Movie[] movies = [Movie("100", null, "10456"), Movie("200", string.Empty, "10456")];
        var existing = Collection(CzechName, "10456");
        var scenario = new Scenario([movies], [existing]);

        await scenario.Run().ConfigureAwait(true);

        Assert.Empty(scenario.Created);
        AssertAdded(scenario, existing.Id, movies);
        Assert.Equal(CzechName, existing.Name);
    }

    [Fact]
    public async Task Run_NewIdWithoutAnyNameHintDoesNotCreateAnUnnamedCollection()
    {
        Movie[] movies = [Movie("100", null, "10456"), Movie("200", string.Empty, "10456")];
        var scenario = new Scenario([movies]);

        await scenario.Run().ConfigureAwait(true);

        Assert.Empty(scenario.Created);
        Assert.Empty(scenario.Additions);
    }

    [Fact]
    public async Task Run_LocalizedCopiesOfOneLogicalMovieDoNotMakeACollection()
    {
        Movie[][] libraries = [[Movie("100", EnglishName, "10456")], [Movie("100", CzechName, "10456")]];
        var scenario = new Scenario(libraries);

        await scenario.Run().ConfigureAwait(true);

        Assert.Empty(scenario.Created);
        Assert.Empty(scenario.Additions);
    }

    [Fact]
    public async Task Run_NameOnlyLegacyGroupCanStillCreateACollectionWithoutInventingAnId()
    {
        Movie[] movies = [Movie("100", EnglishName), Movie("200", EnglishName)];
        var scenario = new Scenario([movies]);

        await scenario.Run().ConfigureAwait(true);

        var created = Assert.Single(scenario.Created);
        Assert.Equal(EnglishName, created.Name);
        Assert.False(created.TryGetProviderId(MetadataProvider.Tmdb, out _));
        AssertAdded(scenario, created.Id, movies);
    }

    private static void AssertAdded(Scenario scenario, Guid collectionId, IEnumerable<Movie> movies)
    {
        var expected = movies.Select(movie => movie.Id).ToHashSet();
        var addition = Assert.Single(scenario.Additions, addition => addition.CollectionId == collectionId);
        Assert.Equal(expected.Count, addition.MovieIds.Length);
        Assert.True(expected.SetEquals(addition.MovieIds));
    }

    private static Movie Movie(string movieId, string? collectionName, string? collectionId = null)
    {
        var movie = new Movie { Id = Guid.NewGuid(), Name = "Movie " + movieId, CollectionName = collectionName };
        movie.SetProviderId(MetadataProvider.Tmdb, movieId);
        if (collectionId is not null)
        {
            movie.SetProviderId(MetadataProvider.TmdbCollection, collectionId);
        }

        return movie;
    }

    private static BoxSet Collection(string name, string? collectionId = null)
    {
        var collection = new BoxSet { Id = Guid.NewGuid(), Name = name };
        if (collectionId is not null)
        {
            collection.SetProviderId(MetadataProvider.Tmdb, collectionId);
        }

        return collection;
    }

    private sealed class Scenario
    {
        private readonly CollectionPostScanTask _task;

        public Scenario(Movie[][] moviesByLibrary, BoxSet[]? existing = null)
        {
            var libraries = moviesByLibrary.Select(_ => new Folder { Id = Guid.NewGuid() }).ToArray();
            var root = new Mock<AggregateFolder>();
            root.SetupGet(value => value.Children).Returns(libraries);
            var library = new Mock<ILibraryManager>(MockBehavior.Strict);
            library.SetupGet(value => value.RootFolder).Returns(root.Object);
            library.Setup(value => value.GetLibraryOptions(It.IsAny<BaseItem>()))
                .Returns(new LibraryOptions { AutomaticallyAddToCollection = true });
            library.Setup(value => value.GetItemList(It.Is<InternalItemsQuery>(query => query.IncludeItemTypes.Contains(BaseItemKind.BoxSet))))
                .Returns((InternalItemsQuery query) =>
                {
                    Assert.Contains(ItemFields.ProviderIds, query.DtoOptions.Fields);
                    return existing ?? Array.Empty<BoxSet>();
                });

            for (var i = 0; i < libraries.Length; i++)
            {
                var libraryId = libraries[i].Id;
                var movies = moviesByLibrary[i];
                library.Setup(value => value.GetItemList(It.Is<InternalItemsQuery>(query => query.ParentId.Equals(libraryId))))
                    .Returns(movies);
            }

            var collections = new Mock<ICollectionManager>(MockBehavior.Strict);
            collections.Setup(value => value.CreateCollectionAsync(It.IsAny<CollectionCreationOptions>()))
                .Returns((CollectionCreationOptions options) =>
                {
                    var collection = new BoxSet
                    {
                        Id = Guid.NewGuid(),
                        Name = options.Name,
                        ProviderIds = new Dictionary<string, string>(options.ProviderIds, StringComparer.OrdinalIgnoreCase)
                    };
                    Created.Add(collection);
                    return Task.FromResult(collection);
                });
            collections.Setup(value => value.AddToCollectionAsync(It.IsAny<Guid>(), It.IsAny<IEnumerable<Guid>>()))
                .Returns((Guid id, IEnumerable<Guid> movieIds) =>
                {
                    Additions.Add((id, movieIds.ToArray()));
                    return Task.CompletedTask;
                });

            _task = new CollectionPostScanTask(library.Object, collections.Object, NullLogger<CollectionPostScanTask>.Instance);
        }

        public List<BoxSet> Created { get; } = new();

        public List<(Guid CollectionId, Guid[] MovieIds)> Additions { get; } = new();

        public Task Run() => _task.Run(Mock.Of<IProgress<double>>(), CancellationToken.None);
    }
}
