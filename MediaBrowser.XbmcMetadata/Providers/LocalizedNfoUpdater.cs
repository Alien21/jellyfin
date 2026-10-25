using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;

namespace MediaBrowser.XbmcMetadata.Providers;

/// <summary>
/// Changes only proven untranslated values in an externally managed NFO.
/// </summary>
internal static class LocalizedNfoUpdater
{
    internal static async Task UpdateAsync(string path, BaseItem item, LocalizedMetadataUpdates updates, CancellationToken cancellationToken)
    {
        if (item.IsLocked || !updates.HasValues || !File.Exists(path))
        {
            return;
        }

        var original = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        Encoding encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        var preambleLength = 0;
        if (original.AsSpan().StartsWith(new byte[] { 0xff, 0xfe, 0x00, 0x00 }))
        {
            encoding = new UTF32Encoding(bigEndian: false, byteOrderMark: false, throwOnInvalidCharacters: true);
            preambleLength = 4;
        }
        else if (original.AsSpan().StartsWith(new byte[] { 0x00, 0x00, 0xfe, 0xff }))
        {
            encoding = new UTF32Encoding(bigEndian: true, byteOrderMark: false, throwOnInvalidCharacters: true);
            preambleLength = 4;
        }
        else if (original.AsSpan().StartsWith(new byte[] { 0xff, 0xfe }))
        {
            encoding = new UnicodeEncoding(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true);
            preambleLength = 2;
        }
        else if (original.AsSpan().StartsWith(new byte[] { 0xfe, 0xff }))
        {
            encoding = new UnicodeEncoding(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: true);
            preambleLength = 2;
        }
        else if (original.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }))
        {
            preambleLength = 3;
        }

        // Never replace undecodable characters in fields that this updater must preserve.
        var content = encoding.GetString(original, preambleLength, original.Length - preambleLength);

        // Kodi movie NFOs can append provider URLs after the XML document.
        // Match BaseNfoParser's search so '>' in a URL stays in the suffix.
        var xmlEnd = content.LastIndexOf("</", StringComparison.Ordinal);
        if (xmlEnd >= 0)
        {
            xmlEnd = content.IndexOf('>', xmlEnd);
        }

        var suffix = xmlEnd >= 0 ? content[(xmlEnd + 1)..] : string.Empty;
        var xml = xmlEnd >= 0 ? content[..(xmlEnd + 1)] : content;
        XDocument document;
        using (var stringReader = new StringReader(xml))
        using (var reader = XmlReader.Create(stringReader, new XmlReaderSettings { Async = true, DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
        {
            document = await XDocument.LoadAsync(reader, LoadOptions.PreserveWhitespace, cancellationToken).ConfigureAwait(false);
        }

        if (document.Declaration?.Encoding is { Length: > 0 } declaredEncoding)
        {
            var compatible = declaredEncoding.ToLowerInvariant() switch
            {
                "utf-8" or "utf8" => encoding.CodePage == 65001,
                "utf-16" => encoding.CodePage is 1200 or 1201,
                "utf-16le" => encoding.CodePage == 1200,
                "utf-16be" => encoding.CodePage == 1201,
                "utf-32" => encoding.CodePage is 12000 or 12001,
                "utf-32le" => encoding.CodePage == 12000,
                "utf-32be" => encoding.CodePage == 12001,
                _ => false
            };
            if (!compatible)
            {
                throw new XmlException("The NFO encoding is not supported for localized updates.");
            }
        }

        var root = document.Root;
        if (root is null)
        {
            return;
        }

        if (root.Elements().Any(element => element.Name.Namespace == root.Name.Namespace
            && string.Equals(element.Name.LocalName, "lockdata", StringComparison.OrdinalIgnoreCase)
            && string.Equals(element.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var lockedFields = item.LockedFields.ToHashSet();
        foreach (var value in root.Elements().Where(element => element.Name.Namespace == root.Name.Namespace
            && string.Equals(element.Name.LocalName, "lockedfields", StringComparison.OrdinalIgnoreCase)).SelectMany(element => element.Value.Split('|')))
        {
            if (Enum.TryParse<MetadataField>(value.Trim(), ignoreCase: true, out var field))
            {
                lockedFields.Add(field);
            }
        }

        var changed = false;
        if (!lockedFields.Contains(MetadataField.Name))
        {
            changed |= UpdateText(root, ["name", "title", "localtitle"], "title", updates.OriginalName, updates.Name);
        }

        if (!lockedFields.Contains(MetadataField.Overview))
        {
            var overviewElement = root.Name.LocalName switch
            {
                "artist" => "biography",
                "album" => "review",
                _ => "plot"
            };
            changed |= UpdateText(root, ["biography", "plot", "review"], overviewElement, updates.OriginalOverview, updates.Overview);
            changed |= UpdateText(root, ["tagline"], "tagline", updates.OriginalTagline, updates.Tagline);
        }

        if (!lockedFields.Contains(MetadataField.Genres) && updates.Genres is { Count: > 0 })
        {
            changed |= UpdateGenres(root, updates.OriginalGenres, updates.Genres);
        }

        if (updates.Images is not null)
        {
            changed |= UpdateImages(root, updates.Images);
        }

        if (!changed)
        {
            return;
        }

        var temporaryPath = Path.Combine(Path.GetDirectoryName(path)!, "." + Path.GetFileName(path) + ".localized-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await using (var writer = XmlWriter.Create(stream, new XmlWriterSettings
                {
                    Async = true,
                    Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
                    Indent = false,
                    CloseOutput = false
                }))
                {
                    await document.SaveAsync(writer, cancellationToken).ConfigureAwait(false);
                }

                if (suffix.Length > 0)
                {
                    await stream.WriteAsync(Encoding.UTF8.GetBytes(suffix), cancellationToken).ConfigureAwait(false);
                }
            }

            // An external manager may have changed the NFO while providers were queried.
            // Preserve that newer document instead of overwriting our stale snapshot.
            var current = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            if (!original.AsSpan().SequenceEqual(current))
            {
                return;
            }

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(temporaryPath, File.GetUnixFileMode(path));
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Replace(temporaryPath, path, null);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private static bool UpdateText(XElement root, IReadOnlyCollection<string> aliases, string canonicalName, string? original, string? localized)
    {
        if (string.IsNullOrWhiteSpace(localized))
        {
            return false;
        }

        var elements = root.Elements().Where(element => element.Name.Namespace == root.Name.Namespace
            && aliases.Contains(element.Name.LocalName, StringComparer.OrdinalIgnoreCase)).ToArray();
        if (elements.Length == 0)
        {
            root.Add(new XElement(root.Name.Namespace + canonicalName, localized));
            return true;
        }

        var changed = false;
        foreach (var element in elements)
        {
            if ((string.IsNullOrWhiteSpace(element.Value) || SameText(element.Value, original)) && !SameText(element.Value, localized))
            {
                element.Value = localized;
                changed = true;
            }
        }

        return changed;
    }

    private static bool UpdateGenres(XElement root, IReadOnlyList<string>? original, IReadOnlyList<string> localized)
    {
        var values = localized.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
        var elements = root.Elements().Where(element => element.Name.Namespace == root.Name.Namespace
            && string.Equals(element.Name.LocalName, "genre", StringComparison.OrdinalIgnoreCase)).ToArray();
        var existing = elements.Select(element => NormalizeText(element.Value)).ToArray();
        var expected = original?.Select(NormalizeText).ToArray() ?? [];
        if (values.Length == 0 || (existing.Any(value => value.Length > 0)
            && !existing.Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(expected.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase))
            || existing.Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(values.Select(NormalizeText).Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        for (var i = 0; i < Math.Min(elements.Length, values.Length); i++)
        {
            elements[i].Value = values[i];
        }

        foreach (var element in elements.Skip(values.Length))
        {
            element.Remove();
        }

        foreach (var value in values.Skip(elements.Length))
        {
            root.Add(new XElement(root.Name.Namespace + "genre", value));
        }

        return true;
    }

    private static bool UpdateImages(XElement root, IReadOnlyList<LocalizedMetadataImageUpdate> updates)
    {
        var changed = false;
        var thumbs = root.Elements().Where(element => element.Name.Namespace == root.Name.Namespace && IsThumb(element))
            .Concat(root.Elements().Where(element => element.Name.Namespace == root.Name.Namespace
                && string.Equals(element.Name.LocalName, "fanart", StringComparison.OrdinalIgnoreCase)).SelectMany(element => element.Elements().Where(child => child.Name.Namespace == root.Name.Namespace && IsThumb(child))));
        foreach (var element in thumbs)
        {
            if (element.Attribute("aspect")?.Value.Contains('.', StringComparison.Ordinal) is true)
            {
                continue;
            }

            var type = GetImageType(element);
            var update = updates.FirstOrDefault(candidate => candidate.Type == type
                && !string.IsNullOrWhiteSpace(candidate.Url)
                && SameImage(element.Value, candidate.OriginalUrl));
            if (update is null || SameImage(element.Value, update.Url))
            {
                continue;
            }

            element.Value = update.Url;
            foreach (var preview in element.Attributes().Where(attribute => attribute.Name.Namespace == XNamespace.None
                && string.Equals(attribute.Name.LocalName, "preview", StringComparison.OrdinalIgnoreCase)))
            {
                if (SameImage(preview.Value, update.OriginalUrl))
                {
                    preview.Value = update.Url;
                }
            }

            changed = true;
        }

        return changed;
    }

    private static bool IsThumb(XElement element) => string.Equals(element.Name.LocalName, "thumb", StringComparison.OrdinalIgnoreCase);

    private static ImageType GetImageType(XElement element)
    {
        var aspect = element.Attribute("aspect")?.Value;
        if (string.IsNullOrWhiteSpace(aspect) && string.Equals(element.Parent?.Name.LocalName, "fanart", StringComparison.OrdinalIgnoreCase))
        {
            return ImageType.Backdrop;
        }

        return aspect switch
        {
            "banner" => ImageType.Banner,
            "clearlogo" => ImageType.Logo,
            "discart" => ImageType.Disc,
            "landscape" => ImageType.Thumb,
            "clearart" => ImageType.Art,
            "fanart" => ImageType.Backdrop,
            _ => ImageType.Primary
        };
    }

    private static bool SameText(string? first, string? second)
        => string.Equals(NormalizeText(first), NormalizeText(second), StringComparison.OrdinalIgnoreCase);

    private static string NormalizeText(string? value)
        => string.Join(' ', (value ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static bool SameImage(string first, string second)
    {
        if (string.IsNullOrWhiteSpace(second))
        {
            return false;
        }

        first = first.Trim();
        second = second.Trim();
        if (string.Equals(first, second, StringComparison.Ordinal))
        {
            return true;
        }

        return Uri.TryCreate(first, UriKind.Absolute, out var firstUri)
            && Uri.TryCreate(second, UriKind.Absolute, out var secondUri)
            && string.Equals(firstUri.Host, "image.tmdb.org", StringComparison.OrdinalIgnoreCase)
            && string.Equals(secondUri.Host, "image.tmdb.org", StringComparison.OrdinalIgnoreCase)
            && firstUri.AbsolutePath.StartsWith("/t/p/", StringComparison.Ordinal)
            && secondUri.AbsolutePath.StartsWith("/t/p/", StringComparison.Ordinal)
            && string.Equals(Path.GetFileName(firstUri.AbsolutePath), Path.GetFileName(secondUri.AbsolutePath), StringComparison.Ordinal);
    }
}
