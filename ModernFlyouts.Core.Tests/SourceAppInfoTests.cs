using ModernFlyouts.Core.AppInformation;
using Xunit;

namespace ModernFlyouts.Core.Tests
{
    public class SourceAppInfoTests
    {
        [Theory]
        [InlineData("com.github.th-ch.youtube-music", "YouTube Music")]
        [InlineData("Spotify.exe", "Spotify")]
        [InlineData("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify", "Spotify")]
        public void FallbackDisplayNameUsesFriendlyAppName(string appUserModelId, string expected)
        {
            Assert.Equal(expected, SourceAppInfo.GetFallbackDisplayName(appUserModelId));
        }
    }
}
