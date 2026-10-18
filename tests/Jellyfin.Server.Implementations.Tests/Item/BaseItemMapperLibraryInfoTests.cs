using System;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Server.Implementations.Item;
using MediaBrowser.Controller.Entities.Movies;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Item;

public class BaseItemMapperLibraryInfoTests
{
    [Fact]
    public void Map_MovieTopParentId_PreservesPhysicalLibraryRootSeparatelyFromParent()
    {
        var entity = new BaseItemEntity
        {
            Id = Guid.NewGuid(),
            ParentId = Guid.NewGuid(),
            TopParentId = Guid.NewGuid(),
            Type = "MediaBrowser.Controller.Entities.Movies.Movie"
        };

        var movie = BaseItemMapper.Map(entity, new Movie(), null);

        Assert.Equal(entity.TopParentId, movie.TopParentId);
        Assert.Equal(entity.ParentId, movie.ParentId);
        Assert.NotEqual(movie.ParentId, movie.TopParentId);
    }
}
