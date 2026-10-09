using System.Net.Http;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.YandexMusic.Api;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YandexMusic.Providers;

/// <summary>
/// Artist metadata provider backed by Yandex Music.
/// </summary>
public sealed class YandexArtistProvider : IRemoteMetadataProvider<MusicArtist, ArtistInfo>, IHasOrder
{
    private readonly YandexMusicApiClient _api;
    public YandexArtistProvider(IHttpClientFactory httpClientFactory, ILogger<YandexArtistProvider> logger)
    {
        _api = new YandexMusicApiClient(httpClientFactory, logger);
    }

    public string Name => Constants.ProviderName;

    public int Order => 1;

    public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(ArtistInfo searchInfo, CancellationToken cancellationToken)
    {
        var directId = GetProviderId(searchInfo) ?? YandexMusicIdParser.ExtractArtistId(searchInfo.Name);
        if (!string.IsNullOrWhiteSpace(directId))
        {
            var direct = await _api.GetArtistAsync(directId, cancellationToken).ConfigureAwait(false);
            var artist = YandexMusicJson.ArtistObject(YandexMusicJson.UnwrapResult(direct));
            var directResult = ToSearchResult(artist, directId);
            return directResult is null ? [] : [directResult];
        }

        if (string.IsNullOrWhiteSpace(searchInfo.Name))
        {
            return [];
        }

        var artists = await _api.SearchArtistsAsync(searchInfo.Name, cancellationToken).ConfigureAwait(false);
        return artists
            .Select(artist => ToSearchResult(artist, null))
            .Where(result => result is not null)
            .Cast<RemoteSearchResult>()
            .OrderByDescending(result => string.Equals(result.Name?.Trim(), searchInfo.Name.Trim(), StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    public async Task<MetadataResult<MusicArtist>> GetMetadata(ArtistInfo info, CancellationToken cancellationToken)
    {
        var result = new MetadataResult<MusicArtist> { Item = new MusicArtist() };
        var artistId = GetProviderId(info) ?? YandexMusicIdParser.ExtractArtistId(info.Name);

        if (string.IsNullOrWhiteSpace(artistId))
        {
            var searchResults = await GetSearchResults(info, cancellationToken).ConfigureAwait(false);
            artistId = searchResults.FirstOrDefault()?.ProviderIds.GetValueOrDefault(Constants.ArtistProviderId);
        }

        if (string.IsNullOrWhiteSpace(artistId))
        {
            return result;
        }

        var baseResponse = YandexMusicJson.UnwrapResult(await _api.GetArtistAsync(artistId, cancellationToken).ConfigureAwait(false));
        var artist = YandexMusicJson.ArtistObject(baseResponse);
        if (artist is null)
        {
            return result;
        }

        result.HasMetadata = true;
        result.Item.ProviderIds[Constants.ArtistProviderId] = artistId;

        var configuration = Plugin.Instance?.Configuration;
        var name = YandexMusicJson.String(artist, "name");
        if (configuration?.ReplaceArtistName != false && !string.IsNullOrWhiteSpace(name))
        {
            result.Item.Name = name;
        }

        var infoResponse = YandexMusicJson.UnwrapResult(await _api.GetArtistInfoAsync(artistId, cancellationToken).ConfigureAwait(false));
        var infoArtist = YandexMusicJson.ArtistObject(infoResponse);

        // Artist genres are normally present on /artists/{id}, while /info can
        // return a reduced artist object without the genres field. Merge both
        // responses instead of preferring /info and accidentally dropping genres.
        var genres = await _api.GetLocalizedGenresAsync(
            [artist, baseResponse, infoArtist, infoResponse],
            cancellationToken).ConfigureAwait(false);
        if (genres.Count > 0)
        {
            result.Item.Genres = genres.ToArray();
        }

        if (configuration?.FetchArtistBiography != false)
        {
            var about = YandexMusicJson.UnwrapResult(await _api.GetArtistAboutAsync(artistId, cancellationToken).ConfigureAwait(false));
            var brief = YandexMusicJson.UnwrapResult(await _api.GetArtistBriefInfoAsync(artistId, cancellationToken).ConfigureAwait(false));
            var biographyArtistName = name ?? info.Name;
            result.Item.Overview = ExtractBiography(about, biographyArtistName)
                ?? ExtractBiography(brief, biographyArtistName);
        }

        return result;
    }

    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
        => _api.GetImageResponseAsync(url, cancellationToken);

    private static string? GetProviderId(ArtistInfo info)
        => info.ProviderIds.TryGetValue(Constants.ArtistProviderId, out var value)
            ? YandexMusicIdParser.ExtractArtistId(value)
            : null;

    private static RemoteSearchResult? ToSearchResult(JsonNode? artist, string? forcedId)
    {
        if (artist is null)
        {
            return null;
        }

        var id = forcedId ?? YandexMusicJson.String(artist, "id");
        var name = YandexMusicJson.String(artist, "name");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var result = new RemoteSearchResult
        {
            Name = name,
            SearchProviderName = Constants.ProviderName,
            ImageUrl = YandexMusicJson.NormalizeImageUrl(
                YandexMusicJson.ImageTemplate(artist),
                Plugin.Instance?.Configuration.GetEffectiveArtistImageSize() ?? 1000)
        };
        result.ProviderIds[Constants.ArtistProviderId] = id;
        return result;
    }

    private static string? ExtractBiography(JsonNode? node, string? artistName)
    {
        string[] biographyFields = ["fullBio", "biography", "briefInfo", "description", "text"];
        foreach (var field in biographyFields)
        {
            var biography = YandexMusicJson.FindFirstString(node, field);
            if (!string.IsNullOrWhiteSpace(biography) && !IsServiceDescription(biography, artistName))
            {
                return biography;
            }
        }

        return null;
    }

    private static bool IsServiceDescription(string? text, string? artistName)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(artistName))
        {
            return false;
        }

        var normalizedText = NormalizeDescriptionText(text);
        var normalizedArtistName = NormalizeDescriptionText(artistName);

        if (!normalizedText.StartsWith(normalizedArtistName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var remainder = normalizedText[normalizedArtistName.Length..].TrimStart();
        if (remainder.Length == 0 || (remainder[0] != ':' && remainder[0] != '—' && remainder[0] != '-'))
        {
            return false;
        }

        remainder = remainder[1..].TrimStart();
        string[] serviceMarkers =
        [
            "самые популярные треки",
            "популярные треки",
            "лучшие треки",
            "лучшие песни",
            "most popular tracks",
            "popular tracks",
            "top tracks"
        ];

        return serviceMarkers.Any(marker => remainder.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeDescriptionText(string value)
        => string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim();
}
