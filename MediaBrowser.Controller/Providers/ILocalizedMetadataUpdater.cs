using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;

namespace MediaBrowser.Controller.Providers;

/// <summary>
/// Updates selected localized values in an existing local metadata document.
/// </summary>
public interface ILocalizedMetadataUpdater
{
    /// <summary>
    /// Persists values fetched in the preferred metadata language without regenerating the document.
    /// </summary>
    /// <param name="item">The item whose existing document is updated.</param>
    /// <param name="updates">The localized values with their untranslated counterparts.</param>
    /// <param name="directoryService">The directory service used to locate the document.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the update.</returns>
    Task UpdateLocalizedMetadataAsync(BaseItem item, LocalizedMetadataUpdates updates, IDirectoryService directoryService, CancellationToken cancellationToken);
}
