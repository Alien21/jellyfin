using System;
using System.IO;
using System.Threading.Tasks;
using Emby.Server.Implementations.Collections;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Collections;

public sealed class CollectionManagerPathTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "jellyfin-collection-path-" + Guid.NewGuid().ToString("N"));
    private readonly Mock<ILibraryMonitor> _monitor = new(MockBehavior.Strict);
    private readonly Mock<IProviderManager> _providers = new(MockBehavior.Strict);

    [Fact]
    public async Task CreateCollectionAsync_ExistingTranslatedCollectionDirectory_PreservesMetadata()
    {
        const string requestedName = "English collection";
        var path = CreateExistingCollection(requestedName);
        var metadataPath = Path.Combine(path, "collection.xml");
        const string metadata = "<Item><LocalTitle>Česká kolekce</LocalTitle><TmdbId>123</TmdbId></Item>";
        await File.WriteAllTextAsync(metadataPath, metadata, TestContext.Current.CancellationToken).ConfigureAwait(true);
        var manager = CreateManager(requestedName, requestedName);
        var created = false;
        manager.CollectionCreated += (_, _) => created = true;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.CreateCollectionAsync(new CollectionCreationOptions
        {
            Name = requestedName
        })).ConfigureAwait(true);

        Assert.Contains("already exists", exception.Message, StringComparison.Ordinal);
        Assert.Contains(path, exception.Message, StringComparison.Ordinal);
        Assert.Equal(metadata, await File.ReadAllTextAsync(metadataPath, TestContext.Current.CancellationToken).ConfigureAwait(true));
        Assert.Single(Directory.GetFiles(path));
        Assert.False(created);
        _monitor.VerifyNoOtherCalls();
        _providers.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("Collection: name")]
    [InlineData("Collection? name")]
    public async Task CreateCollectionAsync_DifferentNamesWithSameSanitizedPath_PreservesExistingCollection(string requestedName)
    {
        const string sanitizedName = "Collection  name";
        var path = CreateExistingCollection(sanitizedName);
        var metadataPath = Path.Combine(path, "collection.xml");
        const string metadata = "<Item><LocalTitle>Existing collection</LocalTitle><TmdbId>456</TmdbId></Item>";
        await File.WriteAllTextAsync(metadataPath, metadata, TestContext.Current.CancellationToken).ConfigureAwait(true);
        var manager = CreateManager(requestedName, sanitizedName);

        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.CreateCollectionAsync(new CollectionCreationOptions
        {
            Name = requestedName
        })).ConfigureAwait(true);

        Assert.Equal(metadata, await File.ReadAllTextAsync(metadataPath, TestContext.Current.CancellationToken).ConfigureAwait(true));
        Assert.Single(Directory.GetDirectories(Path.Combine(_directory, "collections")));
        _monitor.VerifyNoOtherCalls();
        _providers.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CreateCollectionAsync_ExistingFileAtMetadataPath_PreservesFile()
    {
        const string name = "Collection";
        var collectionsPath = Path.Combine(_directory, "collections");
        Directory.CreateDirectory(collectionsPath);
        var path = Path.Combine(collectionsPath, name + " [boxset]");
        const string contents = "existing file";
        await File.WriteAllTextAsync(path, contents, TestContext.Current.CancellationToken).ConfigureAwait(true);
        var manager = CreateManager(name, name);

        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.CreateCollectionAsync(new CollectionCreationOptions
        {
            Name = name
        })).ConfigureAwait(true);

        Assert.Equal(contents, await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken).ConfigureAwait(true));
        Assert.False(Directory.Exists(path));
        _monitor.VerifyNoOtherCalls();
        _providers.VerifyNoOtherCalls();
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
    }

    private string CreateExistingCollection(string folderName)
    {
        var path = Path.Combine(_directory, "collections", folderName + " [boxset]");
        Directory.CreateDirectory(path);
        return path;
    }

    private CollectionManager CreateManager(string requestedName, string sanitizedName)
    {
        var collectionsPath = Path.Combine(_directory, "collections");
        var collectionsFolder = new Folder { Id = Guid.NewGuid(), Path = collectionsPath };
        var root = new Mock<AggregateFolder>();
        root.SetupGet(folder => folder.Children).Returns([collectionsFolder]);
        var library = new Mock<ILibraryManager>(MockBehavior.Strict);
        library.SetupGet(manager => manager.RootFolder).Returns(root.Object);
        var appPaths = new Mock<IApplicationPaths>();
        appPaths.SetupGet(paths => paths.DataPath).Returns(_directory);
        var fileSystem = new Mock<IFileSystem>(MockBehavior.Strict);
        fileSystem.Setup(system => system.GetValidFilename(requestedName)).Returns(sanitizedName);
        fileSystem.Setup(system => system.AreEqual(collectionsPath, collectionsPath)).Returns(true);

        return new CollectionManager(
            library.Object,
            appPaths.Object,
            Mock.Of<ILocalizationManager>(),
            fileSystem.Object,
            _monitor.Object,
            NullLoggerFactory.Instance,
            _providers.Object,
            Mock.Of<ILinkedChildrenService>());
    }
}
