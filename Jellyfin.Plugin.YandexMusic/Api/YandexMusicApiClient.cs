using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.YandexMusic.Configuration;
using MediaBrowser.Common.Net;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YandexMusic.Api;

/// <summary>
/// Minimal resilient client for api.music.yandex.net.
/// </summary>
internal sealed class YandexMusicApiClient
{
    private static readonly object GenreCacheSync = new();
    private static readonly SemaphoreSlim GenreCacheSemaphore = new(1, 1);
    private static readonly Dictionary<string, GenreCacheEntry> GenreCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan GenreCacheLifetime = TimeSpan.FromHours(24);
    private static readonly TimeSpan GenreFailureCacheLifetime = TimeSpan.FromMinutes(15);

    private static readonly object ArtistAlbumCacheSync = new();
    private static readonly SemaphoreSlim ArtistAlbumCacheSemaphore = new(1, 1);
    private static readonly Dictionary<string, ArtistAlbumCacheEntry> ArtistAlbumCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan ArtistAlbumCacheLifetime = TimeSpan.FromMinutes(15);

    // Jellyfin may refresh many albums/artists in parallel. Keep the number of
    // concurrent requests to the unofficial Yandex Music API deliberately low
    // to reduce connection resets and throttling during library scans.
    private static readonly SemaphoreSlim ApiRequestSemaphore = new(3, 3);
    private const int MaxRequestAttempts = 3;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger _logger;

    internal YandexMusicApiClient(IHttpClientFactory httpClientFactory, ILogger logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    private static PluginConfiguration Configuration => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    internal Task<JsonNode?> GetArtistAsync(string artistId, CancellationToken cancellationToken)
        => GetJsonAsync($"/artists/{Uri.EscapeDataString(artistId)}", cancellationToken);

    internal Task<JsonNode?> GetArtistInfoAsync(string artistId, CancellationToken cancellationToken)
        => GetJsonAsync($"/artists/{Uri.EscapeDataString(artistId)}/info", cancellationToken);

    internal Task<JsonNode?> GetArtistAboutAsync(string artistId, CancellationToken cancellationToken)
        => GetJsonAsync($"/artists/{Uri.EscapeDataString(artistId)}/about-artist", cancellationToken);

    internal Task<JsonNode?> GetArtistBriefInfoAsync(string artistId, CancellationToken cancellationToken)
        => GetJsonAsync($"/artists/{Uri.EscapeDataString(artistId)}/brief-info", cancellationToken);

    internal Task<JsonNode?> GetAlbumAsync(string albumId, CancellationToken cancellationToken)
        => GetJsonAsync($"/albums/{Uri.EscapeDataString(albumId)}/with-tracks", cancellationToken);

    internal async Task<IReadOnlyList<JsonNode>> GetArtistAlbumsAsync(
        string artistId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(artistId))
        {
            return [];
        }

        artistId = artistId.Trim();
        var now = DateTime.UtcNow;
        lock (ArtistAlbumCacheSync)
        {
            if (ArtistAlbumCache.TryGetValue(artistId, out var cached) && cached.ExpiresAtUtc > now)
            {
                return cached.Albums;
            }
        }

        await ArtistAlbumCacheSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            now = DateTime.UtcNow;
            lock (ArtistAlbumCacheSync)
            {
                if (ArtistAlbumCache.TryGetValue(artistId, out var cached) && cached.ExpiresAtUtc > now)
                {
                    return cached.Albums;
                }
            }

            var url = $"/artists/{Uri.EscapeDataString(artistId)}/direct-albums?sort-by=year&page=0&page-size=200";
            var root = YandexMusicJson.UnwrapResult(await GetJsonAsync(url, cancellationToken).ConfigureAwait(false));
            var albumsNode = YandexMusicJson.GetPath(root, "albums") as JsonArray ?? root as JsonArray;
            var albums = albumsNode is null
                ? Array.Empty<JsonNode>()
                : albumsNode.Where(item => item is not null).Cast<JsonNode>().ToArray();

            lock (ArtistAlbumCacheSync)
            {
                ArtistAlbumCache[artistId] = new ArtistAlbumCacheEntry(albums, DateTime.UtcNow + ArtistAlbumCacheLifetime);
            }

            return albums;
        }
        finally
        {
            ArtistAlbumCacheSemaphore.Release();
        }
    }

    internal Task<JsonNode?> GetGenresAsync(CancellationToken cancellationToken)
        => GetJsonAsync("/genres", cancellationToken);

    internal Task<IReadOnlyList<string>> GetLocalizedGenresAsync(JsonNode? node, CancellationToken cancellationToken)
        => GetLocalizedGenresAsync([node], cancellationToken);

    internal async Task<IReadOnlyList<string>> GetLocalizedGenresAsync(
        IEnumerable<JsonNode?> nodes,
        CancellationToken cancellationToken)
    {
        var genres = nodes
            .Where(node => node is not null)
            .SelectMany(YandexMusicJson.Genres)
            .Where(genre => !string.IsNullOrWhiteSpace(genre))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (genres.Length == 0)
        {
            return [];
        }

        var language = NormalizeLanguage(Configuration.Language);
        var translations = await GetGenreTranslationsAsync(language, cancellationToken).ConfigureAwait(false);
        if (translations.Count == 0)
        {
            return genres;
        }

        return genres
            .Select(genre => translations.TryGetValue(genre, out var localized) ? localized : genre)
            .Where(genre => !string.IsNullOrWhiteSpace(genre))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal async Task<IReadOnlyList<JsonNode>> SearchArtistsAsync(string query, CancellationToken cancellationToken)
        => await SearchAsync(query, "artist", "artists", cancellationToken).ConfigureAwait(false);

    internal async Task<IReadOnlyList<JsonNode>> SearchAlbumsAsync(string query, CancellationToken cancellationToken)
        => await SearchAsync(query, "album", "albums", cancellationToken).ConfigureAwait(false);

    internal async Task<HttpResponseMessage> GetImageResponseAsync(string url, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(NamedClient.Default);
        return await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<JsonNode>> SearchAsync(
        string query,
        string type,
        string section,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var url = "/search?text=" + Uri.EscapeDataString(query.Trim())
            + "&type=" + Uri.EscapeDataString(type)
            + "&page=0&nocorrect=false";

        var root = YandexMusicJson.UnwrapResult(await GetJsonAsync(url, cancellationToken).ConfigureAwait(false));
        var results = YandexMusicJson.GetPath(root, section, "results") as JsonArray
            ?? YandexMusicJson.GetPath(root, section) as JsonArray
            ?? root as JsonArray;
        if (results is null)
        {
            return [];
        }

        var limit = Math.Clamp(Configuration.SearchLimit, 1, 20);
        return results.Where(item => item is not null).Take(limit).Cast<JsonNode>().ToArray();
    }


    private async Task<IReadOnlyDictionary<string, string>> GetGenreTranslationsAsync(
        string language,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        lock (GenreCacheSync)
        {
            if (GenreCache.TryGetValue(language, out var cached) && cached.ExpiresAtUtc > now)
            {
                return cached.Translations;
            }
        }

        await GenreCacheSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            now = DateTime.UtcNow;
            lock (GenreCacheSync)
            {
                if (GenreCache.TryGetValue(language, out var cached) && cached.ExpiresAtUtc > now)
                {
                    return cached.Translations;
                }
            }

            var root = YandexMusicJson.UnwrapResult(await GetGenresAsync(cancellationToken).ConfigureAwait(false));
            var translations = YandexMusicJson.BuildGenreTranslationMap(root, language);
            var lifetime = translations.Count > 0 ? GenreCacheLifetime : GenreFailureCacheLifetime;
            var entry = new GenreCacheEntry(translations, DateTime.UtcNow + lifetime);

            lock (GenreCacheSync)
            {
                GenreCache[language] = entry;
            }

            if (translations.Count > 0)
            {
                _logger.LogDebug(
                    "Yandex Music genre catalog loaded for {Language}: {Count} aliases",
                    language,
                    translations.Count);
            }
            else
            {
                _logger.LogWarning(
                    "Yandex Music /genres returned no usable translations for {Language}; raw genre values will be used",
                    language);
            }

            return translations;
        }
        finally
        {
            GenreCacheSemaphore.Release();
        }
    }

    private static string NormalizeLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return "ru";
        }

        var normalized = language.Trim().Replace('_', '-').ToLowerInvariant();
        var separator = normalized.IndexOf('-');
        return separator > 0 ? normalized[..separator] : normalized;
    }

    private sealed record GenreCacheEntry(
        IReadOnlyDictionary<string, string> Translations,
        DateTime ExpiresAtUtc);

    private sealed record ArtistAlbumCacheEntry(
        IReadOnlyList<JsonNode> Albums,
        DateTime ExpiresAtUtc);

    private async Task<JsonNode?> GetJsonAsync(string relativeUrl, CancellationToken cancellationToken)
    {
        var requestUri = Constants.ApiBaseUrl + relativeUrl;
        var client = _httpClientFactory.CreateClient(NamedClient.Default);

        for (var attempt = 1; attempt <= MaxRequestAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TimeSpan? retryDelay = null;

            try
            {
                await ApiRequestSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    using var request = CreateJsonRequest(requestUri);
                    using var response = await client.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken).ConfigureAwait(false);

                    if (!response.IsSuccessStatusCode)
                    {
                        if (IsTransientStatusCode(response.StatusCode) && attempt < MaxRequestAttempts)
                        {
                            retryDelay = GetRetryDelay(response, attempt);
                            _logger.LogDebug(
                                "Yandex Music request {Path} returned HTTP {StatusCode} on attempt {Attempt}/{MaxAttempts}; retrying in {DelayMs} ms",
                                relativeUrl,
                                (int)response.StatusCode,
                                attempt,
                                MaxRequestAttempts,
                                (int)retryDelay.Value.TotalMilliseconds);
                        }
                        else
                        {
                            _logger.LogWarning(
                                "Yandex Music request {Path} returned HTTP {StatusCode}{Attempts}",
                                relativeUrl,
                                (int)response.StatusCode,
                                attempt > 1 ? $" after {attempt} attempts" : string.Empty);
                            return null;
                        }
                    }
                    else
                    {
                        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                        return await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
                    }
                }
                finally
                {
                    ApiRequestSemaphore.Release();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException ex) when (attempt < MaxRequestAttempts)
            {
                retryDelay = GetRetryDelay(null, attempt);
                LogTransientRetry(relativeUrl, attempt, retryDelay.Value, ex.Message);
            }
            catch (HttpRequestException ex) when (attempt < MaxRequestAttempts)
            {
                retryDelay = GetRetryDelay(null, attempt);
                LogTransientRetry(relativeUrl, attempt, retryDelay.Value, ex.Message);
            }
            catch (IOException ex) when (attempt < MaxRequestAttempts)
            {
                retryDelay = GetRetryDelay(null, attempt);
                LogTransientRetry(relativeUrl, attempt, retryDelay.Value, ex.Message);
            }
            catch (OperationCanceledException ex)
            {
                _logger.LogWarning(
                    "Yandex Music request failed for {Path} after {Attempts} attempts: {Message}",
                    relativeUrl,
                    attempt,
                    ex.Message);
                return null;
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(
                    "Yandex Music request failed for {Path} after {Attempts} attempts: {Message}",
                    relativeUrl,
                    attempt,
                    ex.Message);
                return null;
            }
            catch (IOException ex)
            {
                _logger.LogWarning(
                    "Yandex Music request failed for {Path} after {Attempts} attempts: {Message}",
                    relativeUrl,
                    attempt,
                    ex.Message);
                return null;
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(
                    "Could not parse Yandex Music response from {Path}: {Message}",
                    relativeUrl,
                    ex.Message);
                return null;
            }

            if (retryDelay.HasValue)
            {
                await Task.Delay(retryDelay.Value, cancellationToken).ConfigureAwait(false);
            }
        }

        return null;
    }

    private static HttpRequestMessage CreateJsonRequest(string requestUri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var config = Configuration;
        if (!string.IsNullOrWhiteSpace(config.OAuthToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("OAuth", config.OAuthToken.Trim());
        }

        if (!string.IsNullOrWhiteSpace(config.Language))
        {
            request.Headers.TryAddWithoutValidation("Accept-Language", config.Language.Trim());
        }

        return request;
    }

    private static bool IsTransientStatusCode(HttpStatusCode statusCode)
        => statusCode is HttpStatusCode.RequestTimeout
            or HttpStatusCode.TooManyRequests
            or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout;

    private static TimeSpan GetRetryDelay(HttpResponseMessage? response, int attempt)
    {
        var retryAfter = response?.Headers.RetryAfter;
        if (retryAfter?.Delta is TimeSpan delta && delta > TimeSpan.Zero)
        {
            return delta > TimeSpan.FromSeconds(10) ? TimeSpan.FromSeconds(10) : delta;
        }

        if (retryAfter?.Date is DateTimeOffset date)
        {
            var until = date - DateTimeOffset.UtcNow;
            if (until > TimeSpan.Zero)
            {
                return until > TimeSpan.FromSeconds(10) ? TimeSpan.FromSeconds(10) : until;
            }
        }

        var baseMilliseconds = attempt switch
        {
            1 => 500,
            2 => 1500,
            _ => 3000
        };

        // A little jitter keeps multiple Jellyfin refresh workers from retrying
        // the same endpoint at exactly the same moment.
        return TimeSpan.FromMilliseconds(baseMilliseconds + Random.Shared.Next(0, 251));
    }

    private void LogTransientRetry(string relativeUrl, int attempt, TimeSpan retryDelay, string message)
    {
        _logger.LogDebug(
            "Transient Yandex Music error for {Path} on attempt {Attempt}/{MaxAttempts}: {Message}. Retrying in {DelayMs} ms",
            relativeUrl,
            attempt,
            MaxRequestAttempts,
            message,
            (int)retryDelay.TotalMilliseconds);
    }
}
