using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.XbmcMetadata.Providers;
using Xunit;

namespace Jellyfin.XbmcMetadata.Tests.Parsers;

public sealed class LocalizedNfoUpdaterTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("jellyfin-localized-nfo-").FullName;

    [Fact]
    public async Task UpdateAsync_ProvenUntranslatedText_PreservesCustomFieldsAndFallback()
    {
        var path = await WriteAsync("""
            <?xml version="1.0" encoding="utf-8"?>
            <movie custom="kept" xmlns:custom="urn:custom">
              <!-- external manager comment -->
              <title>English Name</title>
              <localtitle>Vlastní český název</localtitle>
              <originaltitle>Original Name</originaltitle>
              <sorttitle>My sorting title</sorttitle>
              <plot>English plot without a Czech replacement.</plot>
              <tagline>  English   tagline </tagline>
              <genre>Action</genre>
              <genre>Adventure</genre>
              <customfield source="radarr">preserved</customfield>
              <custom:title>English Name</custom:title>
              <fileinfo><streamdetails><audio><language>eng</language><channels>6</channels></audio></streamdetails></fileinfo>
            </movie>
            """);
        var before = XDocument.Load(path);
        await LocalizedNfoUpdater.UpdateAsync(
            path,
            new Movie(),
            new LocalizedMetadataUpdates
            {
                OriginalName = "English Name",
                Name = "Český název",
                OriginalTagline = "English tagline",
                Tagline = "Český slogan",
                OriginalGenres = ["Action", "Adventure"],
                Genres = ["Akční", "Dobrodružný"]
            },
            TestContext.Current.CancellationToken);

        var after = XDocument.Load(path);
        Assert.Equal("Český název", after.Root!.Element("title")!.Value);
        Assert.Equal("Vlastní český název", after.Root.Element("localtitle")!.Value);
        Assert.Equal("Český slogan", after.Root.Element("tagline")!.Value);
        Assert.Equal(new[] { "Akční", "Dobrodružný" }, after.Root.Elements("genre").Select(element => element.Value));
        foreach (var name in new[] { "originaltitle", "sorttitle", "plot", "customfield", "fileinfo" })
        {
            Assert.True(XNode.DeepEquals(before.Root!.Element(name), after.Root.Element(name)), name);
        }

        Assert.Equal("kept", after.Root.Attribute("custom")!.Value);
        Assert.Equal("English Name", after.Root.Element(XName.Get("title", "urn:custom"))!.Value);
        Assert.Contains(after.Root.Nodes(), node => node is XComment comment && comment.Value.Contains("external manager comment", StringComparison.Ordinal));
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task UpdateAsync_ImageReplacements_PreserveLocalizedVariantsAndOtherArtwork()
    {
        const string oldPoster = "https://image.tmdb.org/t/p/original/english-poster.jpg";
        const string newPoster = "https://image.tmdb.org/t/p/original/czech-poster.jpg";
        const string oldBackdrop = "https://image.tmdb.org/t/p/original/english-landscape.jpg";
        const string newBackdrop = "https://image.tmdb.org/t/p/original/czech-landscape.jpg";
        var path = await WriteAsync($"""
            <movie>
              <title>Český název</title>
              <thumb aspect="poster" preview="https://image.tmdb.org/t/p/w300/english-poster.jpg" custom="keep">https://image.tmdb.org/t/p/w500/english-poster.jpg</thumb>
              <thumb aspect="poster" preview="https://image.tmdb.org/t/p/w300/custom-czech.jpg">https://image.tmdb.org/t/p/original/custom-czech.jpg</thumb>
              <thumb aspect="clearlogo">https://image.tmdb.org/t/p/original/czech-logo.png</thumb>
              <fanart><thumb preview="{oldBackdrop}">{oldBackdrop}</thumb><thumb>https://image.tmdb.org/t/p/original/textless.jpg</thumb></fanart>
              <actor><name>Actor</name><thumb>{oldPoster}</thumb></actor>
            </movie>
            """);
        await LocalizedNfoUpdater.UpdateAsync(
            path,
            new Movie(),
            new LocalizedMetadataUpdates
            {
                Images =
                [
                    new() { Type = ImageType.Primary, OriginalUrl = oldPoster, Url = newPoster },
                    new() { Type = ImageType.Backdrop, OriginalUrl = oldBackdrop, Url = newBackdrop }
                ]
            },
            TestContext.Current.CancellationToken);

        var root = XDocument.Load(path).Root!;
        var posters = root.Elements("thumb").ToArray();
        Assert.Equal(newPoster, posters[0].Value);
        Assert.Equal(newPoster, posters[0].Attribute("preview")!.Value);
        Assert.Equal("keep", posters[0].Attribute("custom")!.Value);
        Assert.Equal("https://image.tmdb.org/t/p/original/custom-czech.jpg", posters[1].Value);
        Assert.Equal("https://image.tmdb.org/t/p/w300/custom-czech.jpg", posters[1].Attribute("preview")!.Value);
        Assert.Equal("https://image.tmdb.org/t/p/original/czech-logo.png", posters[2].Value);
        Assert.Equal(newBackdrop, root.Element("fanart")!.Elements("thumb").First().Value);
        Assert.Equal("https://image.tmdb.org/t/p/original/textless.jpg", root.Element("fanart")!.Elements("thumb").Last().Value);
        Assert.Equal(oldPoster, root.Element("actor")!.Element("thumb")!.Value);
    }

    [Fact]
    public async Task UpdateAsync_KodiProviderLinksAfterXml_ArePreserved()
    {
        const string oldPoster = "https://image.tmdb.org/t/p/original/english-poster.jpg";
        const string newPoster = "https://image.tmdb.org/t/p/original/czech-poster.jpg";
        const string suffix = "\r\nhttps://www.themoviedb.org/movie/64686?label=Česky>Русский\r\nhttps://www.imdb.com/title/tt1335975\r\n";
        var path = await WriteAsync(
            "<movie><title>47 róninů</title><plot>Český popis.</plot>"
            + "<originaltitle>Самурай</originaltitle>"
            + $"<thumb aspect=\"poster\" preview=\"{oldPoster}\">{oldPoster}</thumb></movie>"
            + suffix);

        await LocalizedNfoUpdater.UpdateAsync(
            path,
            new Movie(),
            new LocalizedMetadataUpdates
            {
                OriginalName = "47 Ronin",
                Name = "Jiný český název",
                OriginalOverview = "English plot.",
                Overview = "Jiný český popis.",
                Images = [new() { Type = ImageType.Primary, OriginalUrl = oldPoster, Url = newPoster }]
            },
            TestContext.Current.CancellationToken);

        var updated = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        Assert.EndsWith(suffix, updated, StringComparison.Ordinal);
        var xmlEnd = updated.IndexOf("</movie>", StringComparison.Ordinal) + "</movie>".Length;
        var root = XDocument.Parse(updated[..xmlEnd]).Root!;
        Assert.Equal("47 róninů", root.Element("title")!.Value);
        Assert.Equal("Český popis.", root.Element("plot")!.Value);
        Assert.Equal("Самурай", root.Element("originaltitle")!.Value);
        Assert.Equal(newPoster, root.Element("thumb")!.Value);
        Assert.Equal(newPoster, root.Element("thumb")!.Attribute("preview")!.Value);
    }

    [Theory]
    [InlineData("windows-1250")]
    [InlineData("iso-8859-2")]
    public async Task UpdateAsync_LegacyEncodingDeclaration_PreservesOriginalFile(string encoding)
    {
        var path = await WriteAsync($"<?xml version=\"1.0\" encoding=\"{encoding}\"?><movie><title>English Name</title><custom>kept</custom></movie>");
        var original = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<XmlException>(() => LocalizedNfoUpdater.UpdateAsync(
            path,
            new Movie(),
            new LocalizedMetadataUpdates { OriginalName = "English Name", Name = "Český název" },
            TestContext.Current.CancellationToken));
        Assert.Equal(original, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UpdateAsync_InvalidUtf8_PreservesOriginalFile()
    {
        var path = Path.Combine(_directory, "invalid-encoding.nfo");
        byte[] original = [.. Encoding.UTF8.GetBytes("<movie><title>English Name</title><custom>"), 0xe8, .. Encoding.UTF8.GetBytes("</custom></movie>")];
        await File.WriteAllBytesAsync(path, original, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<DecoderFallbackException>(() => LocalizedNfoUpdater.UpdateAsync(
            path,
            new Movie(),
            new LocalizedMetadataUpdates { OriginalName = "English Name", Name = "Český název" },
            TestContext.Current.CancellationToken));
        Assert.Equal(original, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(1200)]
    [InlineData(1201)]
    [InlineData(12000)]
    [InlineData(12001)]
    public async Task UpdateAsync_UnicodeBom_PreservesCustomTextAndProviderLinks(int codePage)
    {
        var path = Path.Combine(_directory, "unicode.nfo");
        var encoding = Encoding.GetEncoding(codePage);
        const string suffix = "\r\nhttps://www.themoviedb.org/movie/64686?label=Česky>Русский\r\n";
        var content = $"<?xml version=\"1.0\" encoding=\"{encoding.WebName}\"?><movie><title>English Name</title><custom>Česky Русский</custom></movie>" + suffix;
        await File.WriteAllBytesAsync(path, [.. encoding.GetPreamble(), .. encoding.GetBytes(content)], TestContext.Current.CancellationToken);
        await LocalizedNfoUpdater.UpdateAsync(
            path,
            new Movie(),
            new LocalizedMetadataUpdates { OriginalName = "English Name", Name = "Český název" },
            TestContext.Current.CancellationToken);
        var updated = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        Assert.EndsWith(suffix, updated, StringComparison.Ordinal);
        var xmlEnd = updated.IndexOf("</movie>", StringComparison.Ordinal) + "</movie>".Length;
        var root = XDocument.Parse(updated[..xmlEnd]).Root!;
        Assert.Equal("Český název", root.Element("title")!.Value);
        Assert.Equal("Česky Русский", root.Element("custom")!.Value);
    }

    [Fact]
    public async Task UpdateAsync_AlreadyLocalizedOrManualValues_LeavesFileUntouched()
    {
        var path = await WriteAsync("<movie><title>Vlastní český název</title><plot>Český popis</plot><genre>Vlastní žánr</genre></movie>");
        var before = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        await LocalizedNfoUpdater.UpdateAsync(
            path,
            new Movie(),
            new LocalizedMetadataUpdates
            {
                OriginalName = "English Name",
                Name = "Jiný český název",
                OriginalOverview = "English plot",
                Overview = "Jiný český popis",
                OriginalGenres = ["Action"],
                Genres = ["Akční"]
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(before, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("<movie />")]
    [InlineData("<movie><title>  </title><plot /><tagline /><genre> </genre></movie>")]
    public async Task UpdateAsync_MissingOrEmptyValues_AreFilledWithLocalizedValues(string xml)
    {
        var path = await WriteAsync(xml);
        await LocalizedNfoUpdater.UpdateAsync(
            path,
            new Movie(),
            new LocalizedMetadataUpdates
            {
                OriginalName = "English Name",
                Name = "Český název",
                OriginalOverview = "English plot",
                Overview = "Český popis",
                OriginalTagline = "English tagline",
                Tagline = "Český slogan",
                OriginalGenres = ["Action"],
                Genres = ["Akční"]
            },
            TestContext.Current.CancellationToken);

        var root = XDocument.Load(path).Root!;
        Assert.Equal("Český název", root.Element("title")!.Value);
        Assert.Equal("Český popis", root.Element("plot")!.Value);
        Assert.Equal("Český slogan", root.Element("tagline")!.Value);
        Assert.Equal("Akční", Assert.Single(root.Elements("genre")).Value);
    }

    [Theory]
    [InlineData("<lockdata>true</lockdata>")]
    [InlineData("<lockedfields>Name|Overview|Genres</lockedfields>")]
    public async Task UpdateAsync_LocksInNfoDocument_AreRespected(string locks)
    {
        var path = await WriteAsync($"<movie>{locks}<title>English Name</title><plot>English plot</plot><genre>Action</genre></movie>");
        var before = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        await LocalizedNfoUpdater.UpdateAsync(
            path,
            new Movie(),
            new LocalizedMetadataUpdates
            {
                OriginalName = "English Name",
                Name = "Český název",
                OriginalOverview = "English plot",
                Overview = "Český popis",
                OriginalGenres = ["Action"],
                Genres = ["Akční"]
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(before, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UpdateAsync_ReorderedEnglishGenres_AreLocalized()
    {
        var path = await WriteAsync("<movie><genre>Adventure</genre><genre>Action</genre></movie>");
        await LocalizedNfoUpdater.UpdateAsync(
            path,
            new Movie(),
            new LocalizedMetadataUpdates
            {
                OriginalGenres = ["Action", "Adventure"],
                Genres = ["Akční", "Dobrodružný"]
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "Akční", "Dobrodružný" }, XDocument.Load(path).Root!.Elements("genre").Select(element => element.Value));
    }

    [Theory]
    [InlineData("Dobrodružný", "Akční")]
    [InlineData("Action", "Vlastní žánr")]
    public async Task UpdateAsync_LocalizedOrMixedCustomGenres_LeaveDocumentUntouched(string firstGenre, string secondGenre)
    {
        var path = await WriteAsync($"<movie><genre>{firstGenre}</genre><genre>{secondGenre}</genre></movie>");
        var before = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        await LocalizedNfoUpdater.UpdateAsync(
            path,
            new Movie(),
            new LocalizedMetadataUpdates
            {
                OriginalGenres = ["Action", "Adventure"],
                Genres = ["Akční", "Dobrodružný"]
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(before, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UpdateAsync_LockedMetadata_LeavesFileUntouched(bool lockWholeItem)
    {
        var path = await WriteAsync("<movie><title>English Name</title><plot>English plot</plot><genre>Action</genre></movie>");
        var before = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        var item = new Movie
        {
            IsLocked = lockWholeItem,
            LockedFields = lockWholeItem ? [] : [MetadataField.Name, MetadataField.Overview, MetadataField.Genres]
        };
        await LocalizedNfoUpdater.UpdateAsync(
            path,
            item,
            new LocalizedMetadataUpdates
            {
                OriginalName = "English Name",
                Name = "Český název",
                OriginalOverview = "English plot",
                Overview = "Český popis",
                OriginalGenres = ["Action"],
                Genres = ["Akční"]
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(before, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UpdateAsync_MissingDocument_IsNotCreated()
    {
        var path = Path.Combine(_directory, "missing.nfo");
        await LocalizedNfoUpdater.UpdateAsync(path, new Movie(), new LocalizedMetadataUpdates { Name = "Český název" }, TestContext.Current.CancellationToken);
        Assert.False(File.Exists(path));
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task UpdateAsync_MalformedDocument_IsNotReplaced()
    {
        var path = await WriteAsync("<movie><title>English Name</movie>");
        var before = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<XmlException>(() => LocalizedNfoUpdater.UpdateAsync(
            path,
            new Movie(),
            new LocalizedMetadataUpdates
            {
                OriginalName = "English Name",
                Name = "Český název"
            },
            TestContext.Current.CancellationToken));
        Assert.Equal(before, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task UpdateAsync_NoLocalizedFallback_LeavesFileUntouched()
    {
        var path = await WriteAsync("<movie><title>English Name</title><plot>English plot</plot></movie>");
        var before = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        await LocalizedNfoUpdater.UpdateAsync(
            path,
            new Movie(),
            new LocalizedMetadataUpdates
            {
                OriginalName = "English Name",
                Name = string.Empty,
                OriginalOverview = "English plot",
                Overview = null
            },
            TestContext.Current.CancellationToken);
        Assert.Equal(before, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }

    private async Task<string> WriteAsync(string text)
    {
        var path = Path.Combine(_directory, "movie.nfo");
        await File.WriteAllTextAsync(path, text, TestContext.Current.CancellationToken);
        return path;
    }
}
