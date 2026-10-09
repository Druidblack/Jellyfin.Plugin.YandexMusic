using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.YandexMusic.Configuration;

/// <summary>
/// Plugin settings.
/// </summary>
public sealed class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets an optional Yandex Music OAuth token.
    /// </summary>
    public string OAuthToken { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the preferred language sent to the API.
    /// </summary>
    public string Language { get; set; } = "ru";

    /// <summary>
    /// Gets or sets the legacy shared image size. A value of 0 means original quality (orig).
    /// Kept for backward compatibility with settings saved by older plugin versions.
    /// </summary>
    public int ImageSize { get; set; } = 1000;

    /// <summary>
    /// Gets or sets the requested artist image size.
    /// A value of 0 means original quality (orig).
    /// A negative value means "use legacy ImageSize".
    /// </summary>
    public int ArtistImageSize { get; set; } = -1;

    /// <summary>
    /// Gets or sets the requested album image size.
    /// A value of 0 means original quality (orig).
    /// A negative value means "use legacy ImageSize".
    /// </summary>
    public int AlbumImageSize { get; set; } = -1;

    /// <summary>
    /// Gets or sets the maximum number of search results returned to Jellyfin.
    /// </summary>
    public int SearchLimit { get; set; } = 10;

    /// <summary>
    /// Gets or sets whether artist names may be replaced by the Yandex Music value.
    /// </summary>
    public bool ReplaceArtistName { get; set; } = true;

    /// <summary>
    /// Gets or sets whether album names may be replaced by the Yandex Music value.
    /// </summary>
    public bool ReplaceAlbumName { get; set; } = true;

    /// <summary>
    /// Gets or sets whether artist biography text should be downloaded.
    /// </summary>
    public bool FetchArtistBiography { get; set; } = true;

    /// <summary>
    /// Gets or sets whether Yandex Music artist images are also exposed as backdrops.
    /// </summary>
    public bool UseArtistImagesAsBackdrops { get; set; } = false;

    /// <summary>
    /// Gets the effective artist image size with backward compatibility.
    /// </summary>
    public int GetEffectiveArtistImageSize()
        => GetEffectiveImageSize(ArtistImageSize, ImageSize);

    /// <summary>
    /// Gets the effective album image size with backward compatibility.
    /// </summary>
    public int GetEffectiveAlbumImageSize()
        => GetEffectiveImageSize(AlbumImageSize, ImageSize);

    private static int GetEffectiveImageSize(int currentValue, int legacyValue)
    {
        if (currentValue >= 0)
        {
            return NormalizeImageSize(currentValue);
        }

        return NormalizeImageSize(legacyValue);
    }

    private static int NormalizeImageSize(int value)
        => value == 0 ? 0 : 1000;
}
