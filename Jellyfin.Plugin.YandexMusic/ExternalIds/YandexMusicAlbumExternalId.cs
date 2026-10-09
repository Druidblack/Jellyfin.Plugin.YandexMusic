using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.YandexMusic.ExternalIds;

/// <summary>
/// Yandex Music album identifier shown in the Jellyfin metadata editor.
/// </summary>
public sealed class YandexMusicAlbumExternalId : IExternalId
{
    public string ProviderName => Constants.ProviderName;

    public string Key => Constants.AlbumProviderId;

    public ExternalIdMediaType? Type => ExternalIdMediaType.Album;

    public bool Supports(IHasProviderIds item) => item is MusicAlbum;
}
