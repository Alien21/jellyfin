using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;

namespace MediaBrowser.Providers.Manager;

/// <summary>
/// Selects translations only when the old value is a known untranslated provider value.
/// </summary>
internal static class LocalizedMetadataSelector
{
    internal static void AddTextUpdates(BaseItem local, BaseItem translated, BaseItem untranslated, LocalizedMetadataUpdates updates)
    {
        if (local.IsLocked)
        {
            return;
        }

        if (updates.Name is null && !local.LockedFields.Contains(MetadataField.Name)
            && CanTranslate(local.Name, translated.Name, untranslated.Name, translated.OriginalTitle))
        {
            updates.OriginalName = local.Name;
            updates.Name = translated.Name;
        }

        if (updates.Overview is null && !local.LockedFields.Contains(MetadataField.Overview)
            && CanTranslate(local.Overview, translated.Overview, untranslated.Overview))
        {
            updates.OriginalOverview = local.Overview;
            updates.Overview = translated.Overview;
        }

        if (updates.Tagline is null && !local.LockedFields.Contains(MetadataField.Overview)
            && CanTranslate(local.Tagline, translated.Tagline, untranslated.Tagline))
        {
            updates.OriginalTagline = local.Tagline;
            updates.Tagline = translated.Tagline;
        }

        if (updates.Genres is null && !local.LockedFields.Contains(MetadataField.Genres) && translated.Genres.Length > 0
            && (local.Genres.Length == 0 || SameValues(local.Genres, untranslated.Genres))
            && !SameValues(untranslated.Genres, translated.Genres))
        {
            updates.OriginalGenres = local.Genres;
            updates.Genres = translated.Genres;
        }
    }

    internal static IReadOnlyList<LocalizedMetadataImageUpdate> GetImageUpdates(
        IReadOnlyList<(string Url, ImageType Type)> localImages,
        IEnumerable<RemoteImageInfo> remoteImages,
        string language)
    {
        var available = remoteImages.ToArray();
        var updates = new List<LocalizedMetadataImageUpdate>();
        foreach (var (url, type) in localImages)
        {
            var old = available.FirstOrDefault(image => (image.Type == type || (type == ImageType.Backdrop && image.Type == ImageType.Thumb))
                && SameImage(image.Url, url));
            if (old is null || string.IsNullOrWhiteSpace(old.Language) || Matches(old.Language, language))
            {
                continue;
            }

            var translated = available.FirstOrDefault(image => image.Type == old.Type
                && string.Equals(image.ProviderName, old.ProviderName, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(image.Language) && Matches(image.Language, language));
            if (translated is not null && !SameImage(url, translated.Url))
            {
                updates.Add(new LocalizedMetadataImageUpdate { Type = type, OriginalUrl = url, Url = translated.Url });
            }
        }

        return updates;
    }

    internal static void ApplyTextUpdates(BaseItem item, LocalizedMetadataUpdates updates)
    {
        if (item.IsLocked)
        {
            return;
        }

        if (updates.Name is not null && !item.LockedFields.Contains(MetadataField.Name) && SameText(item.Name, updates.OriginalName))
        {
            item.Name = updates.Name;
        }

        if (!item.LockedFields.Contains(MetadataField.Overview))
        {
            if (updates.Overview is not null && SameText(item.Overview, updates.OriginalOverview))
            {
                item.Overview = updates.Overview;
            }

            if (updates.Tagline is not null && SameText(item.Tagline, updates.OriginalTagline))
            {
                item.Tagline = updates.Tagline;
            }
        }

        if (updates.Genres is not null && updates.OriginalGenres is not null
            && !item.LockedFields.Contains(MetadataField.Genres) && SameValues(item.Genres, updates.OriginalGenres))
        {
            item.Genres = updates.Genres.ToArray();
        }
    }

    private static bool CanTranslate(string? current, string? translated, string? untranslated, string? originalTitle = null)
        => !string.IsNullOrWhiteSpace(translated)
            && !SameText(translated, untranslated)
            && (string.IsNullOrWhiteSpace(originalTitle) || !SameText(translated, originalTitle))
            && (string.IsNullOrWhiteSpace(current) || SameText(current, untranslated)
                || (!string.IsNullOrWhiteSpace(originalTitle) && SameText(current, originalTitle)));

    private static bool SameText(string? left, string? right)
        => string.Equals(NormalizeText(left), NormalizeText(right), StringComparison.Ordinal);

    private static string NormalizeText(string? value)
        => string.Join(' ', (value ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static bool SameValues(IEnumerable<string> left, IEnumerable<string> right)
        => left.Select(NormalizeText).Order(StringComparer.Ordinal).SequenceEqual(right.Select(NormalizeText).Order(StringComparer.Ordinal));

    private static bool Matches(string language, string preferred)
        => MetadataLanguageUtils.MatchesPreferredLanguage(language, preferred);

    private static bool SameImage(string left, string right)
        => string.Equals(ImageKey(left), ImageKey(right), StringComparison.Ordinal);

    private static string ImageKey(string value)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && string.Equals(uri.Host, "image.tmdb.org", StringComparison.OrdinalIgnoreCase))
        {
            return "tmdb:" + uri.Segments[^1];
        }

        return value;
    }
}
