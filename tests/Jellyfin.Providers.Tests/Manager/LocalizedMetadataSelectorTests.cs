using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using MediaBrowser.Providers.Manager;
using Xunit;

namespace Jellyfin.Providers.Tests.Manager;

public class LocalizedMetadataSelectorTests
{
    [Fact]
    public void AddTextUpdates_OnlyKnownUntranslatedValuesChanged()
    {
        var local = new Movie { Name = "English title", Overview = "My Czech description", Tagline = "English tagline", Genres = ["Action"] };
        var english = new Movie { Name = "English title", Overview = "English description", Tagline = "English tagline", Genres = ["Action"] };
        var translated = new Movie { Name = "Český název", Overview = "Český popis", Tagline = "Český slogan", Genres = ["Akční"] };
        var updates = new LocalizedMetadataUpdates();

        LocalizedMetadataSelector.AddTextUpdates(local, translated, english, updates);
        LocalizedMetadataSelector.ApplyTextUpdates(local, updates);

        Assert.Equal("Český název", local.Name);
        Assert.Equal("My Czech description", local.Overview);
        Assert.Equal("Český slogan", local.Tagline);
        Assert.Equal(new[] { "Akční" }, local.Genres);
        Assert.Null(updates.Overview);
    }

    [Fact]
    public void AddTextUpdates_EmptyAndFallbackValuesPreserved()
    {
        var local = new Movie { Name = "English title", Overview = "English description", Tagline = "English tagline" };
        var english = new Movie { Name = local.Name, Overview = local.Overview, Tagline = local.Tagline };
        var translated = new Movie { Name = "Original title", OriginalTitle = "Original title", Overview = string.Empty, Tagline = english.Tagline };
        var updates = new LocalizedMetadataUpdates();

        LocalizedMetadataSelector.AddTextUpdates(local, translated, english, updates);

        Assert.False(updates.HasValues);
    }

    [Fact]
    public void ApplyTextUpdates_LockedAndManuallyChangedValuesPreserved()
    {
        var item = new Movie { Name = "English", Overview = "Changed by user", LockedFields = [MetadataField.Name] };
        var updates = new LocalizedMetadataUpdates
        {
            Name = "Česky", OriginalName = "English", Overview = "Český popis", OriginalOverview = "English overview"
        };

        LocalizedMetadataSelector.ApplyTextUpdates(item, updates);

        Assert.Equal("English", item.Name);
        Assert.Equal("Changed by user", item.Overview);
    }

    [Theory]
    [InlineData("en", true)]
    [InlineData("cs", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void GetImageUpdates_OnlyProvenForeignArtworkChanged(string? oldLanguage, bool expectedChange)
    {
        var images = new[]
        {
            new RemoteImageInfo { Type = ImageType.Primary, Url = "https://image.tmdb.org/t/p/original/old.jpg", Language = oldLanguage },
            new RemoteImageInfo { Type = ImageType.Primary, Url = "https://image.tmdb.org/t/p/original/czech.jpg", Language = "cs" }
        };

        var updates = LocalizedMetadataSelector.GetImageUpdates(
            [("https://image.tmdb.org/t/p/w500/old.jpg", ImageType.Primary)], images, "cs");

        Assert.Equal(expectedChange ? 1 : 0, updates.Count);
        if (expectedChange)
        {
            Assert.Equal(images[1].Url, updates[0].Url);
        }
    }

    [Fact]
    public void GetImageUpdates_UnknownImageOrMissingTranslationPreserved()
    {
        var images = new[] { new RemoteImageInfo { Type = ImageType.Primary, Url = "https://image.tmdb.org/t/p/original/old.jpg", Language = "en" } };

        Assert.Empty(LocalizedMetadataSelector.GetImageUpdates([("https://image.tmdb.org/t/p/w500/old.jpg", ImageType.Primary)], images, "cs"));
        Assert.Empty(LocalizedMetadataSelector.GetImageUpdates([("https://example.org/manual.jpg", ImageType.Primary)], images, "cs"));
    }
}
