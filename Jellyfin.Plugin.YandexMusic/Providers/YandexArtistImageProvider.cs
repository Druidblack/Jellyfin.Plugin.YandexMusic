using System.Net.Http;
using Jellyfin.Plugin.YandexMusic.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YandexMusic.Providers;

/// <summary>
/// Artist image provider.
/// </summary>
public sealed class YandexArtistImageProvider : IRemoteImageProvider, IHasOrder
{
    private readonly YandexMusicApiClient _api;

    public YandexArtistImageProvider(IHttpClientFactory httpClientFactory, ILogger<YandexArtistImageProvider> logger)
    {
        _api = new YandexMusicApiClient(httpClientFactory, logger);
    }

    public string Name => Constants.ProviderName;

    public int Order => 1;

    public bool Supports(BaseItem item) => item is MusicArtist;

    public IEnumerable<ImageType> GetSupportedImages(BaseItem item)
    {
        var useBackdrops = Plugin.Instance?.Configuration.UseArtistImagesAsBackdrops ?? false;
        return useBackdrops
            ? [ImageType.Primary, ImageType.Backdrop]
            : [ImageType.Primary];
    }

    public async Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, CancellationToken cancellationToken)
    {
        if (!item.ProviderIds.TryGetValue(Constants.ArtistProviderId, out var rawId))
        {
            return [];
        }

        var artistId = YandexMusicIdParser.ExtractArtistId(rawId);
        if (string.IsNullOrWhiteSpace(artistId))
        {
            return [];
        }

        // /artists/{id} contains the basic cover while /about-artist can expose
        // the complete photo set in covers[]. Keep both sources and deduplicate.
        var artistResult = YandexMusicJson.UnwrapResult(
            await _api.GetArtistAsync(artistId, cancellationToken).ConfigureAwait(false));
        var aboutResult = YandexMusicJson.UnwrapResult(
            await _api.GetArtistAboutAsync(artistId, cancellationToken).ConfigureAwait(false));

        var configuration = Plugin.Instance?.Configuration;
        var imageSize = configuration?.GetEffectiveArtistImageSize() ?? 1000;
        var useBackdrops = configuration?.UseArtistImagesAsBackdrops ?? false;
        var images = new List<RemoteImageInfo>();
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var template in YandexMusicJson.ArtistImageTemplates(artistResult, aboutResult))
        {
            var imageUrl = YandexMusicJson.NormalizeImageUrl(template, imageSize);
            if (string.IsNullOrWhiteSpace(imageUrl) || !seenUrls.Add(imageUrl))
            {
                continue;
            }

            var thumbnailUrl = YandexMusicJson.NormalizeImageUrl(template, 300);
            images.Add(new RemoteImageInfo
            {
                ProviderName = Name,
                Type = ImageType.Primary,
                Url = imageUrl,
                ThumbnailUrl = thumbnailUrl
            });

            if (useBackdrops)
            {
                images.Add(new RemoteImageInfo
                {
                    ProviderName = Name,
                    Type = ImageType.Backdrop,
                    Url = YandexMusicJson.NormalizeImageUrl(template, 0),
                    ThumbnailUrl = thumbnailUrl
                });
            }
        }

        return images;
    }

    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
        => _api.GetImageResponseAsync(url, cancellationToken);
}
