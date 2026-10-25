using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.Library.Validators;

/// <summary>
/// Class CollectionPostScanTask.
/// </summary>
public class CollectionPostScanTask : ILibraryPostScanTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly ICollectionManager _collectionManager;
    private readonly ILogger<CollectionPostScanTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CollectionPostScanTask" /> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="collectionManager">The collection manager.</param>
    /// <param name="logger">The logger.</param>
    public CollectionPostScanTask(
        ILibraryManager libraryManager,
        ICollectionManager collectionManager,
        ILogger<CollectionPostScanTask> logger)
    {
        _libraryManager = libraryManager;
        _collectionManager = collectionManager;
        _logger = logger;
    }

    /// <summary>
    /// Runs the specified progress.
    /// </summary>
    /// <param name="progress">The progress.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Task.</returns>
    public async Task Run(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var collectionMovies = new List<Movie>();

        foreach (var library in _libraryManager.RootFolder.Children)
        {
            if (!_libraryManager.GetLibraryOptions(library).AutomaticallyAddToCollection)
            {
                continue;
            }

            var startIndex = 0;
            var pagesize = 1000;

            while (true)
            {
                var movies = _libraryManager.GetItemList(new InternalItemsQuery
                {
                    MediaTypes = [MediaType.Video],
                    IncludeItemTypes = [BaseItemKind.Movie],
                    IsVirtualItem = false,
                    OrderBy = [(ItemSortBy.SortName, SortOrder.Ascending)],
                    Parent = library,
                    StartIndex = startIndex,
                    Limit = pagesize,
                    Recursive = true,
                    DtoOptions = new DtoOptions(false)
                    {
                        Fields = [ItemFields.ProviderIds],
                        EnableImages = false,
                        EnableUserData = false
                    }
                });

                foreach (var m in movies)
                {
                    if (m is Movie movie && !movie.PrimaryVersionId.HasValue
                        && (!string.IsNullOrWhiteSpace(movie.CollectionName) || GetCollectionId(movie) is not null))
                    {
                        collectionMovies.Add(movie);
                    }
                }

                if (movies.Count < pagesize)
                {
                    break;
                }

                startIndex += pagesize;
            }
        }

        if (collectionMovies.Count == 0)
        {
            progress.Report(100);
            return;
        }

        var boxSets = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.BoxSet],
            CollapseBoxSetItems = false,
            Recursive = true,
            DtoOptions = new DtoOptions(false)
            {
                Fields = [ItemFields.ProviderIds],
                EnableImages = false,
                EnableUserData = false
            }
        }).OfType<BoxSet>().ToList();

        // Older movie metadata may have only a collection name. A name is an identity
        // fallback only when all available metadata agrees on a single TMDb collection.
        var aliases = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var item in collectionMovies.Cast<BaseItem>().Concat(boxSets))
        {
            var name = item is Movie movie ? movie.CollectionName : item.Name;
            var id = GetCollectionId(item);
            if (!string.IsNullOrWhiteSpace(name) && id is not null)
            {
                if (!aliases.TryGetValue(name, out var ids))
                {
                    ids = new HashSet<string>(StringComparer.Ordinal);
                    aliases.Add(name, ids);
                }

                ids.Add(id);
            }
        }

        var groups = new Dictionary<string, CollectionGroup>(StringComparer.Ordinal);
        foreach (var movie in collectionMovies)
        {
            var id = GetCollectionId(movie);
            if (id is null && !string.IsNullOrWhiteSpace(movie.CollectionName)
                && aliases.TryGetValue(movie.CollectionName, out var ids) && ids.Count == 1)
            {
                id = ids.Single();
            }

            var key = id is null ? "name:" + movie.CollectionName : "tmdb:" + id;
            if (!groups.TryGetValue(key, out var group))
            {
                group = new CollectionGroup(id);
                groups.Add(key, group);
            }

            group.Movies.Add(movie);
            if (!string.IsNullOrWhiteSpace(movie.CollectionName))
            {
                group.Names.Add(movie.CollectionName);
            }
        }

        var movieCollectionIds = collectionMovies.Where(movie => GetCollectionId(movie) is not null)
            .GroupBy(movie => movie.Id)
            .ToDictionary(group => group.Key, group => GetCollectionId(group.First())!);
        var numComplete = 0;
        var count = groups.Count;
        foreach (var group in groups.Values)
        {
            var collectionName = group.Names.FirstOrDefault();
            var movies = group.Movies;
            var movieIds = movies.Select(movie => movie.Id).Distinct().ToArray();
            try
            {
                var uniqueMovieCount = BoxSet.GetUniqueItemCount(movies.Cast<BaseItem>().ToArray());
                var boxSet = FindCollection(group, boxSets, aliases, movieCollectionIds, out var ambiguous);
                if (ambiguous)
                {
                    _logger.LogWarning(
                        "Skipped automatic collection {CollectionName} (TMDb {TmdbCollectionId}): existing collection metadata is ambiguous or conflicts with this identity",
                        collectionName,
                        group.TmdbId);
                }
                else if (boxSet is null)
                {
                    // Copies of the same movie in different libraries do not make a collection.
                    if (uniqueMovieCount >= 2 && collectionName is not null)
                    {
                        var options = new CollectionCreationOptions
                        {
                            Name = collectionName,
                        };
                        if (group.TmdbId is not null)
                        {
                            options.SetProviderId(MetadataProvider.Tmdb, group.TmdbId);
                        }

                        boxSet = await _collectionManager.CreateCollectionAsync(options).ConfigureAwait(false);
                        boxSets.Add(boxSet);
                        await _collectionManager.AddToCollectionAsync(boxSet.Id, movieIds).ConfigureAwait(false);
                    }
                    else if (uniqueMovieCount >= 2)
                    {
                        _logger.LogWarning("Skipped automatic TMDb collection {TmdbCollectionId}: its movies have no collection name", group.TmdbId);
                    }
                }
                else if (uniqueMovieCount < 2 && await CanRemoveDuplicateOnlyCollectionAsync(boxSet, movieIds).ConfigureAwait(false))
                {
                    // These children are links to library movies: only the collection metadata is removed.
                    _libraryManager.DeleteItem(boxSet, new DeleteOptions
                    {
                        DeleteFileLocation = true,
                        DeleteFromExternalProvider = false
                    });
                    boxSets.Remove(boxSet);
                }
                else
                {
                    await _collectionManager.AddToCollectionAsync(boxSet.Id, movieIds).ConfigureAwait(false);
                }

                numComplete++;
                double percent = numComplete;
                percent /= count;
                percent *= 100;

                progress.Report(percent);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error refreshing {CollectionName} with {@MovieIds}", collectionName, movieIds);
            }
        }

        progress.Report(100);
    }

    private static string? GetCollectionId(BaseItem item)
    {
        var provider = item is Movie ? MetadataProvider.TmdbCollection : MetadataProvider.Tmdb;
        return int.TryParse(item.GetProviderId(provider)?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0
            ? id.ToString(CultureInfo.InvariantCulture)
            : null;
    }

    private static BoxSet? FindCollection(
        CollectionGroup group,
        IReadOnlyList<BoxSet> boxSets,
        Dictionary<string, HashSet<string>> aliases,
        Dictionary<Guid, string> movieCollectionIds,
        out bool ambiguous)
    {
        ambiguous = false;
        if (group.TmdbId is not null)
        {
            var identified = boxSets.FirstOrDefault(boxSet => GetCollectionId(boxSet) == group.TmdbId);
            if (identified is not null)
            {
                return identified;
            }
        }

        if (group.Names.Any(name => aliases.TryGetValue(name, out var ids) && ids.Count > 1))
        {
            ambiguous = true;
            return null;
        }

        var movieIds = group.Movies.Select(movie => movie.Id).ToHashSet();
        var named = boxSets.Where(boxSet => group.Names.Contains(boxSet.Name)).ToArray();
        if (named.Length > 0)
        {
            // Never merge different provider IDs or create a second collection on the
            // same name-derived metadata directory. Legacy names must be unambiguous.
            if (named.Length == 1 && GetCollectionId(named[0]) is null
                && (group.TmdbId is null || named[0].LinkedChildren.All(child => child.ItemId.HasValue
                    && (movieIds.Contains(child.ItemId.Value)
                        || (movieCollectionIds.TryGetValue(child.ItemId.Value, out var id) && id == group.TmdbId)))))
            {
                return named[0];
            }

            ambiguous = true;
            return null;
        }

        // A translated legacy collection can already contain these films. Without a
        // reliable provider identity, do not guess or recreate its old English name.
        ambiguous = boxSets.Any(boxSet => (group.TmdbId is null || GetCollectionId(boxSet) is null)
            && boxSet.LinkedChildren.Any(child => child.ItemId.HasValue && movieIds.Contains(child.ItemId.Value)));
        return null;
    }

    private async Task<bool> CanRemoveDuplicateOnlyCollectionAsync(BoxSet boxSet, IReadOnlyCollection<Guid> movieIds)
    {
        if (string.IsNullOrWhiteSpace(boxSet.Path)
            || boxSet.LinkedChildren.Length == 0
            || boxSet.LinkedChildren.Any(child => !child.ItemId.HasValue || !movieIds.Contains(child.ItemId.Value)))
        {
            return false;
        }

        var collectionsFolder = await _collectionManager.GetCollectionsFolder(false).ConfigureAwait(false);
        if (collectionsFolder is null
            || string.IsNullOrWhiteSpace(collectionsFolder.Path)
            || !boxSet.ParentId.Equals(collectionsFolder.Id))
        {
            return false;
        }

        var collectionParentPath = Path.GetDirectoryName(Path.GetFullPath(boxSet.Path));
        var collectionsFolderPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(collectionsFolder.Path));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(collectionParentPath, collectionsFolderPath, comparison);
    }

    private sealed class CollectionGroup
    {
        public CollectionGroup(string? tmdbId)
        {
            TmdbId = tmdbId;
        }

        public string? TmdbId { get; }

        public HashSet<string> Names { get; } = new(StringComparer.Ordinal);

        public List<Movie> Movies { get; } = new();
    }
}
