using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ModernFlyouts.Core.AppInformation
{
    public sealed class PlayerIconCache
    {
        private readonly object gate = new();
        private readonly Dictionary<string, PlayerIconCacheEntry> entries = new(StringComparer.OrdinalIgnoreCase);

        public PlayerIconCache(int maxEntries = 16, TimeSpan? ttl = null)
        {
            MaxEntries = Math.Max(1, maxEntries);
            TimeToLive = ttl ?? TimeSpan.FromHours(24);
        }

        public int MaxEntries { get; }

        public TimeSpan TimeToLive { get; }

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

        public bool TryGet(string key, out PlayerIconCacheResult result, DateTimeOffset? utcNow = null)
        {
            result = null;
            string normalizedKey = NormalizeKey(key);
            if (string.IsNullOrWhiteSpace(normalizedKey))
            {
                return false;
            }

            var now = utcNow ?? DateTimeOffset.UtcNow;

            lock (gate)
            {
                EvictExpiredLocked(now);

                if (!entries.TryGetValue(normalizedKey, out var entry))
                {
                    return false;
                }

                entry.LastUsedAtUtc = now;
                result = new PlayerIconCacheResult(entry.DisplayName, entry.IconBytes);
                return true;
            }
        }

        public void Store(string key, string displayName, Stream iconStream, DateTimeOffset? utcNow = null)
        {
            if (iconStream == null)
            {
                return;
            }

            long originalPosition = iconStream.CanSeek ? iconStream.Position : 0;
            try
            {
                if (iconStream.CanSeek)
                {
                    iconStream.Seek(0, SeekOrigin.Begin);
                }

                using MemoryStream copy = new();
                iconStream.CopyTo(copy);
                Store(key, displayName, copy.ToArray(), utcNow);
            }
            finally
            {
                if (iconStream.CanSeek)
                {
                    iconStream.Seek(originalPosition, SeekOrigin.Begin);
                }
            }
        }

        public void Store(string key, string displayName, byte[] iconBytes, DateTimeOffset? utcNow = null)
        {
            string normalizedKey = NormalizeKey(key);
            if (string.IsNullOrWhiteSpace(normalizedKey) || iconBytes == null || iconBytes.Length == 0)
            {
                return;
            }

            var now = utcNow ?? DateTimeOffset.UtcNow;

            lock (gate)
            {
                entries[normalizedKey] = new PlayerIconCacheEntry
                {
                    Key = normalizedKey,
                    DisplayName = displayName ?? string.Empty,
                    IconBytes = iconBytes.ToArray(),
                    CreatedAtUtc = now,
                    LastUsedAtUtc = now
                };

                EvictOverflowLocked();
            }
        }

        private void EvictExpiredLocked(DateTimeOffset utcNow)
        {
            foreach (var pair in entries.ToArray())
            {
                if (utcNow - pair.Value.LastUsedAtUtc > TimeToLive)
                {
                    entries.Remove(pair.Key);
                }
            }
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
            }
        }

        private static string NormalizeKey(string key)
        {
            return (key ?? string.Empty).Trim();
        }

        private sealed class PlayerIconCacheEntry
        {
            public string Key { get; init; }

            public string DisplayName { get; init; }

            public byte[] IconBytes { get; init; }

            public DateTimeOffset CreatedAtUtc { get; init; }

            public DateTimeOffset LastUsedAtUtc { get; set; }
        }
    }

    public sealed class PlayerIconCacheResult
    {
        internal PlayerIconCacheResult(string displayName, byte[] iconBytes)
        {
            DisplayName = displayName ?? string.Empty;
            IconBytes = iconBytes?.ToArray() ?? Array.Empty<byte>();
        }

        public string DisplayName { get; }

        public byte[] IconBytes { get; }

        public MemoryStream CreateStream()
        {
            return new MemoryStream(IconBytes.ToArray(), writable: false);
        }
    }
}
