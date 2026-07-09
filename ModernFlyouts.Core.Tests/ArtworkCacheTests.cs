using ModernFlyouts.Core.Media.Control;
using System;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace ModernFlyouts.Core.Tests
{
    public class ArtworkCacheTests
    {
        [Fact]
        public void DurationRefinementKeepsPreviousArtworkForSameTrack()
        {
            var cache = new ArtworkCache();
            var image = CreateImage();
            var first = Snapshot("Song", TimeSpan.Zero);
            var refined = Snapshot("Song", TimeSpan.FromMinutes(3));

            cache.StoreDecoded(first, image, DateTimeOffset.UnixEpoch);
            var result = cache.GetPreviousArtwork(refined, DateTimeOffset.UnixEpoch.AddSeconds(1));

            Assert.Same(image, result);
        }

        [Fact]
        public void LruCacheDoesNotGrowBeyondCap()
        {
            var cache = new ArtworkCache(maxEntries: 2);

            cache.StoreDecoded(Snapshot("One", TimeSpan.FromMinutes(1)), CreateImage(), DateTimeOffset.UnixEpoch);
            cache.StoreDecoded(Snapshot("Two", TimeSpan.FromMinutes(2)), CreateImage(), DateTimeOffset.UnixEpoch.AddSeconds(1));
            cache.StoreDecoded(Snapshot("Three", TimeSpan.FromMinutes(3)), CreateImage(), DateTimeOffset.UnixEpoch.AddSeconds(2));

            Assert.Equal(2, cache.Count);
        }

        [Fact]
        public void RemovedSessionEvictsUnusedArtwork()
        {
            var cache = new ArtworkCache(maxEntries: 4);
            cache.StoreDecoded(Snapshot("One", TimeSpan.FromMinutes(1), "source-a"), CreateImage(), DateTimeOffset.UnixEpoch);
            cache.StoreDecoded(Snapshot("Two", TimeSpan.FromMinutes(2), "source-b"), CreateImage(), DateTimeOffset.UnixEpoch);

            cache.EvictForRemovedSessions(new[] { "source-b" });

            Assert.Equal(1, cache.Count);
        }

        private static MediaSessionSnapshot Snapshot(string title, TimeSpan duration, string source = "source")
        {
            return new MediaSessionSnapshot
            {
                StableSessionId = source,
                SourceAppUserModelId = source,
                Title = title,
                Artist = "Artist",
                AlbumTitle = "Album",
                EndTime = duration
            };
        }

        private static ImageSource CreateImage()
        {
            var pixels = new byte[] { 255, 255, 255, 255 };
            return BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, pixels, 4);
        }
    }
}
