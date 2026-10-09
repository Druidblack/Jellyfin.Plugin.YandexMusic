using System.Globalization;
using System.Text.Json.Nodes;

namespace Jellyfin.Plugin.YandexMusic.Api;

internal static class YandexMusicJson
{
    internal static JsonNode? UnwrapResult(JsonNode? root)
        => root is JsonObject obj && obj["result"] is not null ? obj["result"] : root;

    internal static JsonNode? GetPath(JsonNode? node, params string[] path)
    {
        var current = node;
        foreach (var part in path)
        {
            if (current is not JsonObject obj || !obj.TryGetPropertyValue(part, out current))
            {
                return null;
            }
        }

        return current;
    }

    internal static string? String(JsonNode? node, params string[] path)
    {
        var value = path.Length == 0 ? node : GetPath(node, path);
        if (value is null)
        {
            return null;
        }

        if (value is JsonValue jsonValue)
        {
            if (jsonValue.TryGetValue<string>(out var text))
            {
                return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
            }

            if (jsonValue.TryGetValue<long>(out var longValue))
            {
                return longValue.ToString(CultureInfo.InvariantCulture);
            }

            if (jsonValue.TryGetValue<int>(out var intValue))
            {
                return intValue.ToString(CultureInfo.InvariantCulture);
            }
        }

        return null;
    }

    internal static int? Int(JsonNode? node, params string[] path)
    {
        var text = String(node, path);
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    internal static DateTime? Date(JsonNode? node, params string[] path)
    {
        var text = String(node, path);
        return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value)
            ? value
            : null;
    }

    internal static string? FindFirstString(JsonNode? node, params string[] propertyNames)
    {
        if (node is null)
        {
            return null;
        }

        var wanted = new HashSet<string>(propertyNames, StringComparer.OrdinalIgnoreCase);
        return FindFirstStringCore(node, wanted, 0);
    }

    private static string? FindFirstStringCore(JsonNode? node, HashSet<string> propertyNames, int depth)
    {
        if (node is null || depth > 8)
        {
            return null;
        }

        if (node is JsonObject obj)
        {
            foreach (var property in obj)
            {
                if (propertyNames.Contains(property.Key))
                {
                    var direct = String(property.Value);
                    if (!string.IsNullOrWhiteSpace(direct))
                    {
                        return direct;
                    }

                    if (property.Value is JsonObject nested)
                    {
                        var nestedText = String(nested, "text") ?? String(nested, "value");
                        if (!string.IsNullOrWhiteSpace(nestedText))
                        {
                            return nestedText;
                        }
                    }
                }
            }

            foreach (var property in obj)
            {
                var found = FindFirstStringCore(property.Value, propertyNames, depth + 1);
                if (!string.IsNullOrWhiteSpace(found))
                {
                    return found;
                }
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                var found = FindFirstStringCore(item, propertyNames, depth + 1);
                if (!string.IsNullOrWhiteSpace(found))
                {
                    return found;
                }
            }
        }

        return null;
    }

    internal static IReadOnlyList<string> ArtistNames(JsonNode? node)
    {
        var artistsNode = GetPath(node, "artists");
        if (artistsNode is not JsonArray artists)
        {
            return [];
        }

        return artists
            .Select(artist => String(artist, "name"))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static IReadOnlyList<string> ArtistIds(JsonNode? node)
    {
        var artistsNode = GetPath(node, "artists");
        if (artistsNode is not JsonArray artists)
        {
            return [];
        }

        return artists
            .Select(artist => String(artist, "id"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Returns the raw genre identifiers/titles supplied by artist or album responses.
    /// Localization is applied later through the official /genres catalog.
    /// </summary>
    internal static IReadOnlyList<string> Genres(JsonNode? node)
    {
        var values = new List<string>();
        var genresNode = GetPath(node, "genres");

        if (genresNode is JsonArray genres)
        {
            foreach (var genre in genres)
            {
                var text = String(genre)
                    ?? String(genre, "id")
                    ?? String(genre, "urlPart")
                    ?? String(genre, "name")
                    ?? String(genre, "title")
                    ?? String(genre, "fullTitle")
                    ?? String(genre, "titles", "ru", "title")
                    ?? String(genre, "titles", "en", "title");

                if (!string.IsNullOrWhiteSpace(text))
                {
                    values.Add(text);
                }
            }
        }

        var singleGenreNode = GetPath(node, "genre");
        var singleGenre = String(singleGenreNode)
            ?? String(singleGenreNode, "id")
            ?? String(singleGenreNode, "urlPart")
            ?? String(singleGenreNode, "name")
            ?? String(singleGenreNode, "title")
            ?? String(singleGenreNode, "fullTitle");

        if (!string.IsNullOrWhiteSpace(singleGenre))
        {
            values.Add(singleGenre);
        }

        return values.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static IReadOnlyDictionary<string, string> BuildGenreTranslationMap(JsonNode? genreCatalog, string language)
    {
        var translations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var normalizedLanguage = NormalizeLanguage(language);

        if (genreCatalog is JsonArray genres)
        {
            foreach (var genre in genres)
            {
                AddGenreTranslations(genre, normalizedLanguage, translations);
            }
        }
        else if (genreCatalog is JsonObject obj)
        {
            if (obj["genres"] is JsonArray nestedGenres)
            {
                foreach (var genre in nestedGenres)
                {
                    AddGenreTranslations(genre, normalizedLanguage, translations);
                }
            }
            else
            {
                AddGenreTranslations(obj, normalizedLanguage, translations);
            }
        }

        return translations;
    }

    private static void AddGenreTranslations(
        JsonNode? genre,
        string language,
        Dictionary<string, string> translations)
    {
        if (genre is not JsonObject genreObject)
        {
            return;
        }

        var localized = String(genreObject, "titles", language, "title")
            ?? String(genreObject, "titles", language, "fullTitle")
            ?? String(genreObject, "title")
            ?? String(genreObject, "fullTitle");

        if (!string.IsNullOrWhiteSpace(localized))
        {
            AddGenreAlias(translations, String(genreObject, "id"), localized);
            AddGenreAlias(translations, String(genreObject, "urlPart"), localized);
            AddGenreAlias(translations, String(genreObject, "title"), localized);
            AddGenreAlias(translations, String(genreObject, "fullTitle"), localized);

            if (genreObject["titles"] is JsonObject titles)
            {
                foreach (var locale in titles)
                {
                    AddGenreAlias(translations, String(locale.Value, "title"), localized);
                    AddGenreAlias(translations, String(locale.Value, "fullTitle"), localized);
                }
            }
        }

        if (genreObject["subGenres"] is JsonArray subGenres)
        {
            foreach (var child in subGenres)
            {
                AddGenreTranslations(child, language, translations);
            }
        }
    }

    private static void AddGenreAlias(Dictionary<string, string> translations, string? alias, string localized)
    {
        if (!string.IsNullOrWhiteSpace(alias))
        {
            translations[alias.Trim()] = localized.Trim();
        }
    }

    private static string NormalizeLanguage(string language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return "ru";
        }

        var normalized = language.Trim().Replace('_', '-').ToLowerInvariant();
        var separator = normalized.IndexOf('-');
        return separator > 0 ? normalized[..separator] : normalized;
    }

    internal static string? ImageTemplate(JsonNode? node)
    {
        return String(node, "ogImage")
            ?? String(node, "coverUri")
            ?? String(node, "cover", "uri")
            ?? String(node, "cover", "url")
            ?? String(node, "artist", "ogImage")
            ?? String(node, "artist", "coverUri")
            ?? String(node, "artist", "cover", "uri");
    }

    /// <summary>
    /// Collects all artist photo templates without walking into nested albums.
    /// The legacy artist endpoint may expose allCovers while /about-artist exposes covers.
    /// </summary>
    internal static IReadOnlyList<string> ArtistImageTemplates(JsonNode? artistResult, JsonNode? aboutResult)
    {
        var templates = new List<string>();

        AddArtistImageFields(ArtistObject(artistResult), templates);
        AddCoverCollection(artistResult, "allCovers", templates);
        AddCoverCollection(artistResult, "covers", templates);

        AddArtistImageFields(ArtistObject(aboutResult), templates);
        AddCoverCollection(aboutResult, "allCovers", templates);
        AddCoverCollection(aboutResult, "covers", templates);

        return templates
            .Where(template => !string.IsNullOrWhiteSpace(template))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void AddArtistImageFields(JsonNode? artist, List<string> templates)
    {
        AddImageTemplate(templates, String(artist, "ogImage"));
        AddImageTemplate(templates, String(artist, "coverUri"));

        var cover = GetPath(artist, "cover");
        AddCoverTemplates(cover, templates);

        AddCoverCollection(artist, "allCovers", templates);
        AddCoverCollection(artist, "covers", templates);
    }

    private static void AddCoverCollection(JsonNode? container, string propertyName, List<string> templates)
    {
        var collection = GetPath(container, propertyName);
        if (collection is not JsonArray covers)
        {
            return;
        }

        foreach (var cover in covers)
        {
            AddCoverTemplates(cover, templates);
        }
    }

    private static void AddCoverTemplates(JsonNode? cover, List<string> templates)
    {
        if (cover is null)
        {
            return;
        }

        AddImageTemplate(templates, String(cover));
        AddImageTemplate(templates, String(cover, "uri"));
        AddImageTemplate(templates, String(cover, "coverUri"));
        AddImageTemplate(templates, String(cover, "url"));

        AddImageArray(templates, GetPath(cover, "itemsUri"));
        AddImageArray(templates, GetPath(cover, "items_uri"));
    }

    private static void AddImageArray(List<string> templates, JsonNode? node)
    {
        if (node is not JsonArray array)
        {
            return;
        }

        foreach (var item in array)
        {
            AddImageTemplate(templates, String(item));
        }
    }

    private static void AddImageTemplate(List<string> templates, string? template)
    {
        if (!string.IsNullOrWhiteSpace(template))
        {
            templates.Add(template.Trim());
        }
    }

    internal static string? NormalizeImageUrl(string? template, int size)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            return null;
        }

        var sizeToken = size <= 0
            ? "orig"
            : $"{Math.Clamp(size, 100, 2000)}x{Math.Clamp(size, 100, 2000)}";
        var url = template.Trim().Replace("%%", sizeToken, StringComparison.Ordinal);

        if (url.StartsWith("//", StringComparison.Ordinal))
        {
            url = "https:" + url;
        }
        else if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                 && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            url = "https://" + url.TrimStart('/');
        }

        return url;
    }

    internal static JsonNode? ArtistObject(JsonNode? result)
    {
        if (result is JsonObject obj)
        {
            if (obj["artist"] is JsonObject artist)
            {
                return artist;
            }

            if (obj["artists"] is JsonArray artists)
            {
                return artists.FirstOrDefault(item => item is JsonObject);
            }

            return obj;
        }

        if (result is JsonArray array)
        {
            return array.FirstOrDefault(item => item is JsonObject);
        }

        return result;
    }

    internal static JsonNode? AlbumObject(JsonNode? result)
    {
        if (result is JsonObject obj)
        {
            if (obj["album"] is JsonObject album)
            {
                return album;
            }

            if (obj["albums"] is JsonArray albums)
            {
                return albums.FirstOrDefault(item => item is JsonObject);
            }

            return obj;
        }

        if (result is JsonArray array)
        {
            return array.FirstOrDefault(item => item is JsonObject);
        }

        return result;
    }
}
