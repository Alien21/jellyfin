using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Dto;

public class BoxSetUniqueItemCountTests : IDisposable
{
    private readonly IServerConfigurationManager? _previousConfigurationManager = BaseItem.ConfigurationManager;
    private readonly IUserDataManager _previousUserDataManager = BaseItem.UserDataManager;
    private readonly User _user = new("user", "auth-provider", "reset-provider");

    public BoxSetUniqueItemCountTests()
    {
        var configurationManager = new Mock<IServerConfigurationManager>();
        configurationManager.Setup(x => x.Configuration).Returns(new ServerConfiguration { EnableLibrarySourceDetails = true });
        BaseItem.ConfigurationManager = configurationManager.Object;
    }

    [Fact]
    public void GetUniqueItemCount_LibraryCopies_CountsSevenMoviesAndRetainsAllTenChildren()
    {
        var movies = Enumerable.Range(1, 7).Select(id => Movie(id.ToString())).ToArray();
        BaseItem[] children = [.. movies, Movie("4"), Movie("5"), Movie("7")];
        var collection = Collection(children);

        Assert.Equal(7, collection.Object.GetUniqueItemCount(_user));
        Assert.Equal(10, collection.Object.GetChildren(_user, true, null).Count);
        collection.Verify(x => x.GetChildren(_user, true, It.IsAny<InternalItemsQuery>()), Times.Exactly(2));
    }

    [Fact]
    public void GetUniqueItemCount_PartialProviderMetadata_MatchesAnySharedMovieIdentifier()
    {
        var first = Movie("100");
        var second = Movie(null, "TT100");
        var third = Movie("100", "tt100");
        var collection = Collection(first, second, third);

        Assert.Equal(1, collection.Object.GetUniqueItemCount(_user));
    }

    [Fact]
    public void GetUniqueItemCount_IdenticalTitlesWithoutSharedIdentifiers_KeepsDistinctMovies()
    {
        var collection = Collection(Movie("100"), Movie("200"), Movie(), Movie());

        Assert.Equal(4, collection.Object.GetUniqueItemCount(_user));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FillUserDataDtoValues_LibraryCopyPlayed_ReportsOneLogicalUnplayedMovie(bool withItemDto)
    {
        BaseItem[] children = [Movie("1"), Movie("1"), Movie("2")];
        var collection = Collection(children);
        var dataManager = new Mock<IUserDataManager>();
        dataManager.Setup(x => x.GetUserDataBatch(children, _user))
            .Returns(new Dictionary<Guid, UserItemData>
            {
                [children[1].Id] = new() { Key = "movie", Played = true }
            });
        BaseItem.UserDataManager = dataManager.Object;
        var userDto = new UserItemDataDto { Key = "collection" };
        var itemDto = withItemDto ? new BaseItemDto { ChildCount = 3 } : null;
        var options = new DtoOptions(false) { Fields = [ItemFields.RecursiveItemCount] };

        collection.Object.FillUserDataDtoValues(userDto, new UserItemData { Key = "collection" }, itemDto!, _user, options, (1, 3));

        Assert.Equal(1, userDto.UnplayedItemCount);
        Assert.Equal(50, userDto.PlayedPercentage);
        Assert.False(userDto.Played);
        if (itemDto is not null)
        {
            Assert.Equal(2, itemDto.UniqueItemCount);
            Assert.Equal(3, itemDto.ChildCount);
            Assert.Equal(3, itemDto.RecursiveItemCount);
        }

        dataManager.Verify(x => x.GetUserDataBatch(children, _user), Times.Once);
        collection.Verify(x => x.GetChildren(_user, true, It.IsAny<InternalItemsQuery>()), Times.Once);
    }

    [Fact]
    public void FillUserDataDtoValues_MixedCollection_PreservesRecursivePlayCounts()
    {
        var collection = Collection(Movie("1"), new Series { Id = Guid.NewGuid() });
        var userDto = new UserItemDataDto { Key = "collection" };
        var itemDto = new BaseItemDto();

        collection.Object.FillUserDataDtoValues(userDto, new UserItemData { Key = "collection" }, itemDto, _user, new DtoOptions(false), (2, 9));

        Assert.Equal(2, itemDto.UniqueItemCount);
        Assert.Equal(7, userDto.UnplayedItemCount);
    }

    [Theory]
    [InlineData(false, 7)]
    [InlineData(true, 0)]
    public void FillUserDataDtoValues_SevenMoviesTenCopies_CountsUniqueUnplayedMovies(bool played, int expectedUnplayed)
    {
        BaseItem[] children = [.. Enumerable.Range(1, 7).Select(id => Movie(id.ToString())), Movie("4"), Movie("5"), Movie("7")];
        var collection = Collection(children);
        var dataManager = new Mock<IUserDataManager>();
        dataManager.Setup(x => x.GetUserDataBatch(children, _user))
            .Returns(children.ToDictionary(child => child.Id, child => new UserItemData { Key = child.Id.ToString(), Played = played }));
        BaseItem.UserDataManager = dataManager.Object;
        var userDto = new UserItemDataDto { Key = "collection" };
        var itemDto = new BaseItemDto();

        collection.Object.FillUserDataDtoValues(userDto, new UserItemData { Key = "collection" }, itemDto, _user, new DtoOptions(false), (played ? 10 : 0, 10));

        Assert.Equal(7, itemDto.UniqueItemCount);
        Assert.Equal(expectedUnplayed, userDto.UnplayedItemCount);
        Assert.Equal(played, userDto.Played);
        Assert.Equal(played ? 100 : 0, userDto.PlayedPercentage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FillUserDataDtoValues_FeatureDisabled_KeepsOriginalCountsWithoutReadingChildren(bool withItemDto)
    {
        BaseItem.ConfigurationManager.Configuration.EnableLibrarySourceDetails = false;
        var collection = Collection(Movie("1"), Movie("1"), Movie("2"));
        var dataManager = new Mock<IUserDataManager>(MockBehavior.Strict);
        BaseItem.UserDataManager = dataManager.Object;
        var userDto = new UserItemDataDto { Key = "collection" };
        var itemDto = withItemDto ? new BaseItemDto() : null;
        var options = new DtoOptions(false) { Fields = [ItemFields.RecursiveItemCount] };

        collection.Object.FillUserDataDtoValues(userDto, new UserItemData { Key = "collection" }, itemDto!, _user, options, (1, 3));

        Assert.Equal(2, userDto.UnplayedItemCount);
        Assert.Equal(1d / 3 * 100, userDto.PlayedPercentage);
        Assert.False(userDto.Played);
        if (itemDto is not null)
        {
            Assert.Null(itemDto.UniqueItemCount);
            Assert.Equal(3, itemDto.RecursiveItemCount);
        }

        collection.Verify(x => x.GetChildren(_user, true, It.IsAny<InternalItemsQuery>()), Times.Never);
        dataManager.VerifyNoOtherCalls();
    }
    public void Dispose()
    {
        BaseItem.UserDataManager = _previousUserDataManager;
        BaseItem.ConfigurationManager = _previousConfigurationManager!;
    }

    private Mock<BoxSet> Collection(params BaseItem[] children)
    {
        var collection = new Mock<BoxSet> { CallBase = true };
        collection.Object.Id = Guid.NewGuid();
        collection.Setup(x => x.GetChildren(_user, true, It.IsAny<InternalItemsQuery>())).Returns(children);
        return collection;
    }

    private static Movie Movie(string? tmdb = null, string? imdb = null)
    {
        var movie = new Movie { Id = Guid.NewGuid(), Name = "Same title" };
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
