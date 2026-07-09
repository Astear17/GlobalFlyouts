using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Storage.Streams;

namespace ModernFlyouts.Core.Media.Control
{
    public sealed class ArtworkCache
    {
        private readonly object gate = new();
        private readonly Dictionary<string, ArtworkCacheEntry> entries = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> logicalIdentityToKey = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> retryCounts = new(StringComparer.OrdinalIgnoreCase);

        public ArtworkCache(int maxEntries = 64, TimeSpan? ttl = null, TimeSpan? nullThumbnailGracePeriod = null)
        {
            MaxEntries = Math.Max(1, maxEntries);
            TimeToLive = ttl ?? TimeSpan.FromHours(12);
            NullThumbnailGracePeriod = nullThumbnailGracePeriod ?? TimeSpan.FromSeconds(8);
        }

        public int MaxEntries { get; }

        public TimeSpan TimeToLive { get; }

        public TimeSpan NullThumbnailGracePeriod { get; }

        public int Count
        {
            get
            {
                lock (gate)
                {
                    return entries.Count;
                }
            }
        }

        public ImageSource GetPreviousArtwork(MediaSessionSnapshot snapshot, DateTimeOffset utcNow)
        {
            if (snapshot == null)
            {
                return null;
            }

            lock (gate)
            {
                EvictExpiredLocked(utcNow);

                string logicalIdentity = GetLogicalIdentity(snapshot);
                if (logicalIdentityToKey.TryGetValue(logicalIdentity, out string existingKey) &&
                    entries.TryGetValue(existingKey, out var entry) &&
                    utcNow - entry.LastUsedAtUtc <= NullThumbnailGracePeriod)
                {
                    entry.LastUsedAtUtc = utcNow;
                    MediaDiagnostics.Trace($"Artwork cache grace hit for {logicalIdentity}");
                    return entry.Image;
                }
            }

            return null;
        }

        public void StoreDecoded(MediaSessionSnapshot snapshot, ImageSource image, DateTimeOffset? utcNow = null)
        {
            if (snapshot == null || image == null)
            {
                return;
            }

            var now = utcNow ?? DateTimeOffset.UtcNow;
            string key = ResolveStableKey(snapshot, now);
            Store(key, GetLogicalIdentity(snapshot), image, now);
        }

        public async Task<ImageSource> GetOrDecodeAsync(
            MediaSessionSnapshot snapshot,
            IRandomAccessStreamReference thumbnail,
            Func<BitmapSource, BitmapSource> transform = null,
            DateTimeOffset? utcNow = null)
        {
            var now = utcNow ?? DateTimeOffset.UtcNow;

            if (snapshot == null)
            {
                return null;
            }

            string key = ResolveStableKey(snapshot, now);

            lock (gate)
            {
                EvictExpiredLocked(now);

                if (entries.TryGetValue(key, out var entry))
                {
                    entry.LastUsedAtUtc = now;
                    MediaDiagnostics.Trace($"Artwork cache hit: {key}");
                    return entry.Image;
                }
            }

            if (thumbnail == null)
            {
                MediaDiagnostics.Trace($"Artwork thumbnail missing for {key}");
                return GetPreviousArtwork(snapshot, now);
            }

            try
            {
                var image = await DecodeThumbnailAsync(thumbnail, transform);

                if (image == null)
                {
                    ScheduleRetry(key);
                    return GetPreviousArtwork(snapshot, now);
                }

                Store(key, GetLogicalIdentity(snapshot), image, now);
                MediaDiagnostics.Info($"Artwork decode success: {key}");
                return image;
            }
            catch (Exception ex)
            {
                ScheduleRetry(key);
                MediaDiagnostics.Exception($"Artwork decode failed for {key}", ex);
                return GetPreviousArtwork(snapshot, now);
            }
        }

        public void EvictForRemovedSessions(IEnumerable<string> activeSourceAppUserModelIds)
        {
            var active = new HashSet<string>(activeSourceAppUserModelIds ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);

            lock (gate)
            {
                foreach (var pair in entries.ToArray())
                {
                    if (!active.Contains(pair.Value.SourceAppUserModelId))
                    {
                        entries.Remove(pair.Key);
                        retryCounts.Remove(pair.Key);
                        MediaDiagnostics.Info($"Artwork cache evicted removed session entry: {pair.Key}");
                    }
                }

                RebuildLogicalIndexLocked();
            }
        }

        public static string CreateKey(MediaSessionSnapshot snapshot)
        {
            if (snapshot == null)
            {
                return string.Empty;
            }

            string durationPart = snapshot.EndTime > TimeSpan.Zero
                ? Math.Round(snapshot.EndTime.TotalSeconds).ToString("0")
                : "unknown";

            return string.Join("|",
                NormalizePart(snapshot.SourceAppUserModelId),
                NormalizePart(snapshot.Title),
                NormalizePart(snapshot.Artist),
                NormalizePart(snapshot.AlbumTitle),
                durationPart);
        }

        private string ResolveStableKey(MediaSessionSnapshot snapshot, DateTimeOffset utcNow)
        {
            string logicalIdentity = GetLogicalIdentity(snapshot);
            string requestedKey = CreateKey(snapshot);

            lock (gate)
            {
                EvictExpiredLocked(utcNow);

                if (logicalIdentityToKey.TryGetValue(logicalIdentity, out string existingKey))
                {
                    if (entries.ContainsKey(existingKey))
                    {
                        return existingKey;
                    }

                    logicalIdentityToKey.Remove(logicalIdentity);
                }

                logicalIdentityToKey[logicalIdentity] = requestedKey;
                return requestedKey;
            }
        }

        private void Store(string key, string logicalIdentity, ImageSource image, DateTimeOffset utcNow)
        {
            if (image == null)
            {
                return;
            }

            if (image.CanFreeze)
            {
                image.Freeze();
            }

            lock (gate)
            {
                entries[key] = new ArtworkCacheEntry
                {
                    Key = key,
                    LogicalIdentity = logicalIdentity,
                    SourceAppUserModelId = key.Split('|').FirstOrDefault() ?? string.Empty,
                    Image = image,
                    CreatedAtUtc = utcNow,
                    LastUsedAtUtc = utcNow
                };
                logicalIdentityToKey[logicalIdentity] = key;
                retryCounts.Remove(key);
                EvictOverflowLocked();
            }
        }

        private void ScheduleRetry(string key)
        {
            lock (gate)
            {
                retryCounts.TryGetValue(key, out int retries);

                if (retries >= 3)
                {
                    MediaDiagnostics.Warning($"Artwork retry limit reached: {key}");
                    return;
                }

                retryCounts[key] = retries + 1;
                MediaDiagnostics.Trace($"Artwork retry scheduled: {key}, attempt {retries + 1}");
            }
        }

        private static async Task<ImageSource> DecodeThumbnailAsync(
            IRandomAccessStreamReference thumbnail,
            Func<BitmapSource, BitmapSource> transform)
        {
            using var stream = await thumbnail.OpenReadAsync();
            if (stream == null || stream.Size == 0)
            {
                return null;
            }

            using var reader = new DataReader(stream);
            await reader.LoadAsync((uint)stream.Size);

            var buffer = new byte[(int)stream.Size];
            reader.ReadBytes(buffer);

            using var managedStream = new MemoryStream(buffer);
            var bitmap = BitmapFrame.Create(managedStream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);

            if (bitmap == null)
            {
                return null;
            }

            BitmapSource result = transform?.Invoke(bitmap) ?? bitmap;
            return result;
        }

        private void EvictExpiredLocked(DateTimeOffset utcNow)
        {
            foreach (var pair in entries.ToArray())
            {
                if (utcNow - pair.Value.LastUsedAtUtc > TimeToLive)
                {
                    entries.Remove(pair.Key);
                    retryCounts.Remove(pair.Key);
                    MediaDiagnostics.Info($"Artwork cache TTL eviction: {pair.Key}");
                }
            }

            RebuildLogicalIndexLocked();
        }

        private void EvictOverflowLocked()
        {
            while (entries.Count > MaxEntries)
            {
                var oldest = entries.Values
                    .OrderBy(x => x.LastUsedAtUtc)
                    .ThenBy(x => x.CreatedAtUtc)
                    .First();

                entries.Remove(oldest.Key);
                retryCounts.Remove(oldest.Key);
                MediaDiagnostics.Info($"Artwork cache LRU eviction: {oldest.Key}");
            }

            RebuildLogicalIndexLocked();
        }

        private void RebuildLogicalIndexLocked()
        {
            logicalIdentityToKey.Clear();

            foreach (var entry in entries.Values.OrderBy(x => x.LastUsedAtUtc))
            {
                logicalIdentityToKey[entry.LogicalIdentity] = entry.Key;
            }
        }

        private static string GetLogicalIdentity(MediaSessionSnapshot snapshot)
        {
            return string.Join("|",
                NormalizePart(snapshot.SourceAppUserModelId),
                NormalizePart(snapshot.Title),
                NormalizePart(snapshot.Artist),
                NormalizePart(snapshot.AlbumTitle));
        }

        private static string NormalizePart(string value)
        {
            return (value ?? string.Empty).Trim();
        }

        private sealed class ArtworkCacheEntry
        {
            public string Key { get; init; }

            public string LogicalIdentity { get; init; }

            public string SourceAppUserModelId { get; init; }

            public ImageSource Image { get; init; }

            public DateTimeOffset CreatedAtUtc { get; init; }

            public DateTimeOffset LastUsedAtUtc { get; set; }
        }
    }
}
