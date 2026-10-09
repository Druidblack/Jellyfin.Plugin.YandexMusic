using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.YandexMusic.Api;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YandexMusic.Providers;

/// <summary>
/// Album metadata provider backed by Yandex Music.
/// </summary>
public sealed class YandexAlbumProvider : IRemoteMetadataProvider<MusicAlbum, AlbumInfo>, IHasOrder
{
    private readonly YandexMusicApiClient _api;
    private readonly ILogger<YandexAlbumProvider> _logger;

    public YandexAlbumProvider(IHttpClientFactory httpClientFactory, ILogger<YandexAlbumProvider> logger)
    {
        _api = new YandexMusicApiClient(httpClientFactory, logger);
        _logger = logger;
    }

    public string Name => Constants.ProviderName;

    public int Order => 1;

    public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(AlbumInfo searchInfo, CancellationToken cancellationToken)
    {
        // An album URL/id typed into Identify is an explicit request and should be trusted.
        var explicitId = YandexMusicIdParser.ExtractAlbumId(searchInfo.Name);
        if (!string.IsNullOrWhiteSpace(explicitId))
        {
            var direct = await GetAlbumObjectAsync(explicitId, cancellationToken).ConfigureAwait(false);
            var directResult = ToSearchResult(direct, explicitId);
            return directResult is null ? [] : [directResult];
        }

        var artistId = GetArtistProviderId(searchInfo);
        var candidates = new List<JsonNode>();

        // If Jellyfin already has an album id, keep it in manual search results, but in
        // refreshes only trust it when it still belongs to this album.
        var storedId = GetProviderId(searchInfo);
        if (!string.IsNullOrWhiteSpace(storedId))
        {
            var storedAlbum = await GetAlbumObjectAsync(storedId, cancellationToken).ConfigureAwait(false);
            if (storedAlbum is not null && IsStrongAlbumMatch(storedAlbum, searchInfo, artistId))
            {
                candidates.Add(storedAlbum);
            }
            else if (storedAlbum is not null)
            {
                _logger.LogDebug(
                    "Ignoring stale Yandex Music album id {AlbumId} for {Album}; stored album is {StoredAlbum}",
                    storedId,
                    searchInfo.Name,
                    YandexMusicJson.String(storedAlbum, "title"));
            }
        }

        if (string.IsNullOrWhiteSpace(searchInfo.Name))
        {
            return candidates
                .Select(album => ToSearchResult(album, null))
                .Where(result => result is not null)
                .Cast<RemoteSearchResult>()
                .ToArray();
        }

        // AlbumInfo has a dedicated ArtistProviderIds dictionary. Prefer the exact
        // Yandex artist discography when Jellyfin already knows YandexMusicArtist.
        if (!string.IsNullOrWhiteSpace(artistId))
        {
            var artistAlbums = await _api.GetArtistAlbumsAsync(artistId, cancellationToken).ConfigureAwait(false);
            candidates.AddRange(artistAlbums.Where(album => TitleMatches(YandexMusicJson.String(album, "title"), searchInfo)));
        }

        var albumArtist = searchInfo.AlbumArtists.FirstOrDefault();
        var query = string.IsNullOrWhiteSpace(albumArtist)
            ? searchInfo.Name
            : searchInfo.Name + " " + albumArtist;
        candidates.AddRange(await _api.SearchAlbumsAsync(query, cancellationToken).ConfigureAwait(false));

        return DistinctAlbums(candidates)
            .OrderByDescending(album => ScoreAlbum(album, searchInfo, artistId))
            .Select(album => ToSearchResult(album, null))
            .Where(result => result is not null)
            .Cast<RemoteSearchResult>()
            .ToArray();
    }

    public async Task<MetadataResult<MusicAlbum>> GetMetadata(AlbumInfo info, CancellationToken cancellationToken)
    {
        var result = new MetadataResult<MusicAlbum> { Item = new MusicAlbum() };
        var artistId = GetArtistProviderId(info);

        if (HasArtistIdentifyLeak(info))
        {
            _logger.LogDebug(
                "Detected artist Identify SearchResult leaking into album lookup for {Path}: transient name '{Name}' will not be trusted; using song album tags/path instead",
                info.Path,
                info.Name);
        }

        JsonNode? album = null;
        string? albumId = null;

        // Explicit album URL/id entered by a user takes precedence over automatic matching.
        var explicitId = YandexMusicIdParser.ExtractAlbumId(info.Name);
        if (!string.IsNullOrWhiteSpace(explicitId))
        {
            album = await GetAlbumObjectAsync(explicitId, cancellationToken).ConfigureAwait(false);
            albumId = album is null ? null : explicitId;
        }

        // Existing provider ids are not blindly trusted during refreshes.
        // This is important because one bad match would otherwise become permanent.
        if (album is null)
        {
            var storedId = GetProviderId(info);
            if (!string.IsNullOrWhiteSpace(storedId))
            {
                var storedAlbum = await GetAlbumObjectAsync(storedId, cancellationToken).ConfigureAwait(false);
                if (storedAlbum is not null && IsStrongAlbumMatch(storedAlbum, info, artistId))
                {
                    album = storedAlbum;
                    albumId = storedId;
                }
                else if (storedAlbum is not null)
                {
                    _logger.LogDebug(
                        "Yandex Music album id {AlbumId} no longer matches {Album}; searching for a replacement",
                        storedId,
                        info.Name);
                }
            }
        }

        if (album is null)
        {
            var best = await FindBestAutomaticAlbumAsync(info, artistId, cancellationToken).ConfigureAwait(false);
            if (best is not null)
            {
                album = best;
                albumId = YandexMusicJson.String(best, "id");
            }
        }

        if (album is null || string.IsNullOrWhiteSpace(albumId))
        {
            return result;
        }

        // direct-albums/search results are abbreviated; fetch the complete album before
        // applying metadata so genres/date/description remain as rich as before.
        var fullAlbum = await GetAlbumObjectAsync(albumId, cancellationToken).ConfigureAwait(false);
        if (fullAlbum is not null)
        {
            album = fullAlbum;
        }

        result.HasMetadata = true;
        result.Item.ProviderIds[Constants.AlbumProviderId] = albumId;

        var configuration = Plugin.Instance?.Configuration;
        var title = YandexMusicJson.String(album, "title");
        if (configuration?.ReplaceAlbumName != false && !string.IsNullOrWhiteSpace(title))
        {
            result.Item.Name = title;
        }

        var artists = YandexMusicJson.ArtistNames(album);
        if (artists.Count > 0)
        {
            result.Item.AlbumArtists = artists.ToArray();
        }

        var genres = await _api.GetLocalizedGenresAsync(album, cancellationToken).ConfigureAwait(false);
        if (genres.Count > 0)
        {
            result.Item.Genres = genres.ToArray();
        }

        var releaseDate = YandexMusicJson.Date(album, "releaseDate");
        var year = YandexMusicJson.Int(album, "year") ?? releaseDate?.Year;
        if (releaseDate.HasValue)
        {
            result.Item.PremiereDate = releaseDate;
        }

        if (year.HasValue)
        {
            result.Item.ProductionYear = year;
        }

        result.Item.Overview = YandexMusicJson.FindFirstString(album, "description", "descriptionHtml");
        return result;
    }

    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
        => _api.GetImageResponseAsync(url, cancellationToken);

    private async Task<JsonNode?> FindBestAutomaticAlbumAsync(
        AlbumInfo info,
        string? artistId,
        CancellationToken cancellationToken)
    {
        var trustedAlbumName = GetTrustedAlbumName(info);
        if (string.IsNullOrWhiteSpace(trustedAlbumName))
        {
            return null;
        }

        // First search only inside the exact artist's direct discography. This avoids
        // Yandex search/autocorrect returning another artist's similarly named album.
        if (!string.IsNullOrWhiteSpace(artistId))
        {
            var artistAlbums = await _api.GetArtistAlbumsAsync(artistId, cancellationToken).ConfigureAwait(false);
            var exactArtistAlbum = artistAlbums
                .Where(album => IsStrongAlbumMatch(album, info, artistId))
                .OrderByDescending(album => ScoreAlbum(album, info, artistId))
                .FirstOrDefault();

            if (exactArtistAlbum is not null)
            {
                return exactArtistAlbum;
            }
        }

        var albumArtist = info.AlbumArtists.FirstOrDefault();
        var query = string.IsNullOrWhiteSpace(albumArtist)
            ? trustedAlbumName
            : trustedAlbumName + " " + albumArtist;
        var searchAlbums = await _api.SearchAlbumsAsync(query, cancellationToken).ConfigureAwait(false);

        // Automated refresh must never take the first fuzzy result just because Yandex
        // returned something. Require a strong title + artist match before persisting an id.
        var strongMatches = searchAlbums
            .Where(album => IsStrongAlbumMatch(album, info, artistId))
            .OrderByDescending(album => ScoreAlbum(album, info, artistId))
            .ToArray();

        if (strongMatches.Length == 0)
        {
            _logger.LogDebug(
                "No strong Yandex Music album match for {Album} ({Artist}); provider id will not be changed",
                info.Name,
                albumArtist);
            return null;
        }

        return strongMatches[0];
    }

    private async Task<JsonNode?> GetAlbumObjectAsync(string albumId, CancellationToken cancellationToken)
        => YandexMusicJson.AlbumObject(
            YandexMusicJson.UnwrapResult(
                await _api.GetAlbumAsync(albumId, cancellationToken).ConfigureAwait(false)));

    private static string? GetProviderId(AlbumInfo info)
        => info.ProviderIds.TryGetValue(Constants.AlbumProviderId, out var value)
            ? YandexMusicIdParser.ExtractAlbumId(value)
            : null;

    private static string? GetArtistProviderId(AlbumInfo info)
        => info.ArtistProviderIds.TryGetValue(Constants.ArtistProviderId, out var value)
            ? YandexMusicIdParser.ExtractArtistId(value)
            : null;

    private static RemoteSearchResult? ToSearchResult(JsonNode? album, string? forcedId)
    {
        if (album is null)
        {
            return null;
        }

        var id = forcedId ?? YandexMusicJson.String(album, "id");
        var title = YandexMusicJson.String(album, "title");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var artists = YandexMusicJson.ArtistNames(album);
        var releaseDate = YandexMusicJson.Date(album, "releaseDate");
        var year = YandexMusicJson.Int(album, "year") ?? releaseDate?.Year;

        var result = new RemoteSearchResult
        {
            Name = title,
            SearchProviderName = Constants.ProviderName,
            ProductionYear = year,
            PremiereDate = releaseDate,
            ImageUrl = YandexMusicJson.NormalizeImageUrl(
                YandexMusicJson.ImageTemplate(album),
                Plugin.Instance?.Configuration.GetEffectiveAlbumImageSize() ?? 1000),
            Artists = artists.Select(name => new RemoteSearchResult { Name = name }).ToArray(),
            AlbumArtist = artists.Count > 0 ? new RemoteSearchResult { Name = artists[0] } : null
        };

        result.ProviderIds[Constants.AlbumProviderId] = id;
        return result;
    }

    private static bool IsStrongAlbumMatch(JsonNode album, AlbumInfo info, string? expectedArtistId)
    {
        var actualTitle = YandexMusicJson.String(album, "title");
        if (!TitleMatches(actualTitle, info))
        {
            return false;
        }

        var actualArtistIds = YandexMusicJson.ArtistIds(album);
        if (!string.IsNullOrWhiteSpace(expectedArtistId))
        {
            // A candidate that identifies its artists must contain the exact Yandex artist id.
            if (actualArtistIds.Count > 0)
            {
                return actualArtistIds.Contains(expectedArtistId, StringComparer.OrdinalIgnoreCase);
            }
        }

        var expectedArtists = info.AlbumArtists
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(NormalizeText)
            .Where(name => name.Length > 0)
            .ToArray();

        if (expectedArtists.Length == 0)
        {
            return true;
        }

        var actualArtists = YandexMusicJson.ArtistNames(album)
            .Select(NormalizeText)
            .Where(name => name.Length > 0)
            .ToArray();

        return actualArtists.Any(actual => expectedArtists.Contains(actual, StringComparer.Ordinal));
    }

    private static int ScoreAlbum(JsonNode album, AlbumInfo info, string? expectedArtistId)
    {
        var score = 0;
        if (TitleMatches(YandexMusicJson.String(album, "title"), info))
        {
            score += 100;
        }

        var artistIds = YandexMusicJson.ArtistIds(album);
        if (!string.IsNullOrWhiteSpace(expectedArtistId)
            && artistIds.Contains(expectedArtistId, StringComparer.OrdinalIgnoreCase))
        {
            score += 60;
        }
        else
        {
            var expectedArtists = info.AlbumArtists.Select(NormalizeText).Where(name => name.Length > 0).ToArray();
            var actualArtists = YandexMusicJson.ArtistNames(album).Select(NormalizeText).Where(name => name.Length > 0).ToArray();
            if (actualArtists.Any(actual => expectedArtists.Contains(actual, StringComparer.Ordinal)))
            {
                score += 30;
            }
        }

        var candidateYear = YandexMusicJson.Int(album, "year") ?? YandexMusicJson.Date(album, "releaseDate")?.Year;
        if (info.Year.HasValue && candidateYear.HasValue && info.Year.Value == candidateYear.Value)
        {
            score += 10;
        }

        return score;
    }

    private static bool TitleMatches(string? actualTitle, AlbumInfo info)
    {
        var actual = NormalizeText(actualTitle);
        if (actual.Length == 0)
        {
            return false;
        }

        return ExpectedTitles(info)
            .Select(NormalizeText)
            .Where(title => title.Length > 0)
            .Any(title => string.Equals(actual, title, StringComparison.Ordinal));
    }

    private static IEnumerable<string> ExpectedTitles(AlbumInfo info)
    {
        // SongInfo.Album comes from the child audio items and is not overwritten by
        // MetadataService.ApplySearchResult(). During an artist Identify Jellyfin may
        // reuse the artist SearchResult while recursively refreshing child albums,
        // which overwrites AlbumInfo.Name with the artist name. Prefer song album tags
        // as the stable source of truth and completely ignore that transient name when
        // an artist provider id has leaked into the album's ProviderIds dictionary.
        var songAlbumTitles = info.SongInfos
            .Select(song => song.Album)
            .Where(title => !string.IsNullOrWhiteSpace(title))
            .Cast<string>()
            .GroupBy(title => NormalizeText(title), StringComparer.Ordinal)
            .OrderByDescending(group => group.Count())
            .Select(group => group.First())
            .ToArray();

        foreach (var title in songAlbumTitles)
        {
            yield return title;
        }

        var artistIdentifyLeak = HasArtistIdentifyLeak(info);
        if (!artistIdentifyLeak && !string.IsNullOrWhiteSpace(info.Name))
        {
            yield return info.Name;
            var withoutYear = StripTrailingYear(info.Name, info.Year);
            if (!string.Equals(withoutYear, info.Name, StringComparison.Ordinal))
            {
                yield return withoutYear;
            }
        }

        if (!string.IsNullOrWhiteSpace(info.OriginalTitle))
        {
            yield return info.OriginalTitle;
        }

        if (!string.IsNullOrWhiteSpace(info.Path))
        {
            var path = info.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var folderName = Path.GetFileName(path);
            if (!string.IsNullOrWhiteSpace(folderName))
            {
                yield return folderName;
                var withoutYear = StripTrailingYear(folderName, info.Year);
                if (!string.Equals(withoutYear, folderName, StringComparison.Ordinal))
                {
                    yield return withoutYear;
                }
            }
        }
    }

    private static string? GetTrustedAlbumName(AlbumInfo info)
    {
        var songTitle = info.SongInfos
            .Select(song => song.Album)
            .Where(title => !string.IsNullOrWhiteSpace(title))
            .Cast<string>()
            .GroupBy(title => NormalizeText(title), StringComparer.Ordinal)
            .OrderByDescending(group => group.Count())
            .Select(group => group.First())
            .FirstOrDefault();

        if (!string.IsNullOrWhiteSpace(songTitle))
        {
            return songTitle;
        }

        if (!HasArtistIdentifyLeak(info) && !string.IsNullOrWhiteSpace(info.Name))
        {
            return info.Name;
        }

        if (!string.IsNullOrWhiteSpace(info.OriginalTitle))
        {
            return info.OriginalTitle;
        }

        if (!string.IsNullOrWhiteSpace(info.Path))
        {
            var path = info.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var folderName = Path.GetFileName(path);
            if (!string.IsNullOrWhiteSpace(folderName))
            {
                return StripTrailingYear(folderName, info.Year);
            }
        }

        return info.Name;
    }

    private static bool HasArtistIdentifyLeak(AlbumInfo info)
        => info.ProviderIds.ContainsKey(Constants.ArtistProviderId)
            && !info.ProviderIds.ContainsKey(Constants.AlbumProviderId);

    private static string StripTrailingYear(string value, int? year)
    {
        if (!year.HasValue)
        {
            return value;
        }

        var yearText = year.Value.ToString(CultureInfo.InvariantCulture);
        var trimmed = value.Trim();
        string[] suffixes = [$" ({yearText})", $" [{yearText}]", $" - {yearText}"];
        foreach (var suffix in suffixes)
        {
            if (trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return trimmed[..^suffix.Length].TrimEnd();
            }
        }

        return value;
    }

    private static string NormalizeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var decomposed = value.Trim().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
            }
        }

        return builder.ToString();
    }

    private static IReadOnlyList<JsonNode> DistinctAlbums(IEnumerable<JsonNode> albums)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<JsonNode>();
        foreach (var album in albums)
        {
            var id = YandexMusicJson.String(album, "id");
            if (string.IsNullOrWhiteSpace(id) || !seen.Add(id))
            {
                continue;
            }

            result.Add(album);
        }

        return result;
    }
}
