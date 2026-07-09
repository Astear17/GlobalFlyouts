using ModernFlyouts.Core.AppInformation;
using System;
using System.IO;
using Xunit;

namespace ModernFlyouts.Core.Tests
{
    public class PlayerIconCacheTests
    {
        [Fact]
        public void CacheHitReturnsDisplayNameAndReusableStream()
        {
            var cache = new PlayerIconCache();
            byte[] iconBytes = { 1, 2, 3, 4 };

            cache.Store("com.github.th-ch.youtube-music", "YouTube Music", iconBytes, DateTimeOffset.UnixEpoch);

            Assert.True(cache.TryGet("com.github.th-ch.youtube-music", out var result, DateTimeOffset.UnixEpoch.AddMinutes(1)));
            Assert.Equal("YouTube Music", result.DisplayName);

            using MemoryStream stream = result.CreateStream();
            Assert.Equal(iconBytes, stream.ToArray());
        }

        [Fact]
        public void ExpiredEntriesAreEvicted()
        {
            var cache = new PlayerIconCache(ttl: TimeSpan.FromMinutes(5));
            cache.Store("source", "Source", new byte[] { 1 }, DateTimeOffset.UnixEpoch);

            bool hit = cache.TryGet("source", out _, DateTimeOffset.UnixEpoch.AddMinutes(6));

            Assert.False(hit);
            Assert.Equal(0, cache.Count);
        }

        [Fact]
        public void LruCacheDoesNotGrowBeyondCap()
        {
            var cache = new PlayerIconCache(maxEntries: 2);

            cache.Store("one", "One", new byte[] { 1 }, DateTimeOffset.UnixEpoch);
            cache.Store("two", "Two", new byte[] { 2 }, DateTimeOffset.UnixEpoch.AddSeconds(1));
            cache.Store("three", "Three", new byte[] { 3 }, DateTimeOffset.UnixEpoch.AddSeconds(2));

            Assert.Equal(2, cache.Count);
            Assert.False(cache.TryGet("one", out _, DateTimeOffset.UnixEpoch.AddSeconds(3)));
            Assert.True(cache.TryGet("two", out _, DateTimeOffset.UnixEpoch.AddSeconds(3)));
            Assert.True(cache.TryGet("three", out _, DateTimeOffset.UnixEpoch.AddSeconds(3)));
        }
    }
}
