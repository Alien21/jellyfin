using System;
using System.Collections.Generic;
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
        var collectionNameMoviesMap = new Dictionary<string, List<BaseItem>>();

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
                    if (m is Movie movie && !string.IsNullOrEmpty(movie.CollectionName) && !movie.PrimaryVersionId.HasValue)
                    {
                        if (collectionNameMoviesMap.TryGetValue(movie.CollectionName, out var movieList))
                        {
                            movieList.Add(movie);
                        }
                        else
                        {
                            collectionNameMoviesMap[movie.CollectionName] = [movie];
                        }
                    }
                }

                if (movies.Count < pagesize)
                {
                    break;
                }

                startIndex += pagesize;
            }
        }

        var numComplete = 0;
        var count = collectionNameMoviesMap.Count;

        if (count == 0)
        {
            progress.Report(100);
            return;
        }

        var boxSets = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.BoxSet],
            CollapseBoxSetItems = false,
            Recursive = true
        });

        foreach (var (collectionName, movies) in collectionNameMoviesMap)
        {
            var movieIds = movies.Select(movie => movie.Id).Distinct().ToArray();
            try
            {
                var uniqueMovieCount = BoxSet.GetUniqueItemCount(movies);
                var boxSet = boxSets.FirstOrDefault(b => b?.Name == collectionName) as BoxSet;
                if (boxSet is null)
                {
                    // Copies of the same movie in different libraries do not make a collection.
                    if (uniqueMovieCount >= 2)
                    {
                        boxSet = await _collectionManager.CreateCollectionAsync(new CollectionCreationOptions
                        {
                            Name = collectionName,
                        }).ConfigureAwait(false);

                        await _collectionManager.AddToCollectionAsync(boxSet.Id, movieIds).ConfigureAwait(false);
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
}
