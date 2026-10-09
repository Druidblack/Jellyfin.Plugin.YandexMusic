using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.YandexMusic.Api;

internal static partial class YandexMusicIdParser
{
    [GeneratedRegex(@"(?:^|/)(?:artist|artists)/(\d+)(?:[/?#]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ArtistUrlRegex();

    [GeneratedRegex(@"(?:^|/)(?:album|albums)/(\d+)(?:[/?#]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AlbumUrlRegex();

    internal static string? ExtractArtistId(string? value) => Extract(value, ArtistUrlRegex());

    internal static string? ExtractAlbumId(string? value) => Extract(value, AlbumUrlRegex());

    private static string? Extract(string? value, Regex urlRegex)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim();
        if (text.All(char.IsDigit))
        {
            return text;
        }

        var match = urlRegex.Match(text);
        return match.Success ? match.Groups[1].Value : null;
    }
}
