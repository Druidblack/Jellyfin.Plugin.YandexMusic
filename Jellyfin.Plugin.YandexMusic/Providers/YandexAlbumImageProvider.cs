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
/// Album cover provider.
/// </summary>
public sealed class YandexAlbumImageProvider : IRemoteImageProvider, IHasOrder
{
    private readonly YandexMusicApiClient _api;

    public YandexAlbumImageProvider(IHttpClientFactory httpClientFactory, ILogger<YandexAlbumImageProvider> logger)
    {
        _api = new YandexMusicApiClient(httpClientFactory, logger);
    }

    public string Name => Constants.ProviderName;

    public int Order => 1;

    public bool Supports(BaseItem item) => item is MusicAlbum;

    public IEnumerable<ImageType> GetSupportedImages(BaseItem item) => [ImageType.Primary];

    public async Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, CancellationToken cancellationToken)
    {
        if (!item.ProviderIds.TryGetValue(Constants.AlbumProviderId, out var rawId))
        {
            return [];
        }

        var albumId = YandexMusicIdParser.ExtractAlbumId(rawId);
        if (string.IsNullOrWhiteSpace(albumId))
        {
            return [];
        }

        var album = YandexMusicJson.AlbumObject(YandexMusicJson.UnwrapResult(await _api.GetAlbumAsync(albumId, cancellationToken).ConfigureAwait(false)));
        var imageTemplate = YandexMusicJson.ImageTemplate(album);
        var imageUrl = YandexMusicJson.NormalizeImageUrl(imageTemplate, Plugin.Instance?.Configuration.GetEffectiveAlbumImageSize() ?? 1000);

        if (string.IsNullOrWhiteSpace(imageUrl))
        {
            return [];
        }

        return
        [
            new RemoteImageInfo
            {
                ProviderName = Name,
                Type = ImageType.Primary,
                Url = imageUrl,
                ThumbnailUrl = YandexMusicJson.NormalizeImageUrl(imageTemplate, 300)
            }
        ];
    }

    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
        => _api.GetImageResponseAsync(url, cancellationToken);
}
