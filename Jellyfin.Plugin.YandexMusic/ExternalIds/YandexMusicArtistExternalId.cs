using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.YandexMusic.ExternalIds;

/// <summary>
/// Yandex Music artist identifier shown in the Jellyfin metadata editor.
/// </summary>
public sealed class YandexMusicArtistExternalId : IExternalId
{
    public string ProviderName => Constants.ProviderName;

    public string Key => Constants.ArtistProviderId;

    public ExternalIdMediaType? Type => ExternalIdMediaType.Artist;

    public bool Supports(IHasProviderIds item) => item is MusicArtist;
}
