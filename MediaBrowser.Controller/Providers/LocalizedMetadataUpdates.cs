using System.Collections.Generic;

namespace MediaBrowser.Controller.Providers;

/// <summary>
/// Preferred-language values and the proven untranslated values they may replace.
/// </summary>
public sealed class LocalizedMetadataUpdates
{
    /// <summary>Gets or sets the localized name.</summary>
    public string? Name { get; set; }

    /// <summary>Gets or sets the untranslated name that may be replaced.</summary>
    public string? OriginalName { get; set; }

    /// <summary>Gets or sets the localized overview.</summary>
    public string? Overview { get; set; }

    /// <summary>Gets or sets the untranslated overview that may be replaced.</summary>
    public string? OriginalOverview { get; set; }

    /// <summary>Gets or sets the localized tagline.</summary>
    public string? Tagline { get; set; }

    /// <summary>Gets or sets the untranslated tagline that may be replaced.</summary>
    public string? OriginalTagline { get; set; }

    /// <summary>Gets or sets the localized genre names.</summary>
    public IReadOnlyList<string>? Genres { get; set; }

    /// <summary>Gets or sets the untranslated genres that may be replaced.</summary>
    public IReadOnlyList<string>? OriginalGenres { get; set; }

    /// <summary>Gets or sets proven untranslated image URLs and their localized replacements.</summary>
    public IReadOnlyList<LocalizedMetadataImageUpdate>? Images { get; set; }

    /// <summary>Gets a value indicating whether any localized values were supplied.</summary>
    public bool HasValues => !string.IsNullOrWhiteSpace(Name)
        || !string.IsNullOrWhiteSpace(Overview)
        || !string.IsNullOrWhiteSpace(Tagline)
        || Genres is { Count: > 0 }
        || Images is { Count: > 0 };
}
