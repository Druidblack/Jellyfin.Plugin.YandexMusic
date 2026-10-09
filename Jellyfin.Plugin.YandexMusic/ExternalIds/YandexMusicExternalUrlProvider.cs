using Jellyfin.Plugin.YandexMusic.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Providers;

namespace Jellyfin.Plugin.YandexMusic.ExternalIds;

/// <summary>
/// Adds links from Jellyfin items to the matching Yandex Music page.
/// </summary>
public sealed class YandexMusicExternalUrlProvider : IExternalUrlProvider
{
    public string Name => Constants.ProviderName;

    public IEnumerable<string> GetExternalUrls(BaseItem item)
    {
        if (item is MusicArtist
            && item.ProviderIds.TryGetValue(Constants.ArtistProviderId, out var rawArtistId))
        {
            var artistId = YandexMusicIdParser.ExtractArtistId(rawArtistId);
            if (!string.IsNullOrWhiteSpace(artistId))
            {
                yield return $"{Constants.WebBaseUrl}/artist/{Uri.EscapeDataString(artistId)}";
            }
        }

        if (item is MusicAlbum
            && item.ProviderIds.TryGetValue(Constants.AlbumProviderId, out var rawAlbumId))
        {
            var albumId = YandexMusicIdParser.ExtractAlbumId(rawAlbumId);
            if (!string.IsNullOrWhiteSpace(albumId))
            {
                yield return $"{Constants.WebBaseUrl}/album/{Uri.EscapeDataString(albumId)}";
            }
        }
    }
}
