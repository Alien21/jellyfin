using MediaBrowser.Model.Entities;

namespace MediaBrowser.Controller.Providers;

/// <summary>
/// A provider-verified untranslated image and its preferred-language replacement.
/// </summary>
public sealed class LocalizedMetadataImageUpdate
{
    /// <summary>Gets or sets the image type.</summary>
    public ImageType Type { get; set; }

    /// <summary>Gets or sets the untranslated image URL that may be replaced.</summary>
    public string OriginalUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the preferred-language image URL.</summary>
    public string Url { get; set; } = string.Empty;
}
