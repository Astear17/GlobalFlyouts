using System;
using System.Windows.Media;
using Windows.Media.Control;

namespace ModernFlyouts.Core.Media.Control
{
    public sealed record MediaSessionSnapshot
    {
        public string StableSessionId { get; init; } = string.Empty;

        public string SourceAppUserModelId { get; init; } = string.Empty;

        public string DisplayAppName { get; init; } = string.Empty;

        public string Title { get; init; } = string.Empty;

        public string Artist { get; init; } = string.Empty;

        public string AlbumTitle { get; init; } = string.Empty;

        public ImageSource ThumbnailImage { get; init; }

        public MediaSessionPlaybackStatus PlaybackStatus { get; init; } = MediaSessionPlaybackStatus.Unknown;

        public MediaPlaybackType PlaybackType { get; init; } = MediaPlaybackType.Unknown;

        public bool CanPlay { get; init; }

        public bool CanPause { get; init; }

        public bool CanTogglePlayPause => CanPlay || CanPause;

        public bool CanNext { get; init; }

        public bool CanPrevious { get; init; }

        public bool CanStop { get; init; }

        public bool CanSeek { get; init; }

        public bool CanChangeShuffle { get; init; }

        public bool CanChangeRepeat { get; init; }

        public bool? ShuffleActive { get; init; }

        public MediaPlaybackAutoRepeatMode RepeatMode { get; init; } = MediaPlaybackAutoRepeatMode.None;

        public TimeSpan StartTime { get; init; } = TimeSpan.Zero;

        public TimeSpan EndTime { get; init; } = TimeSpan.Zero;

        public TimeSpan MinSeekTime { get; init; } = TimeSpan.Zero;

        public TimeSpan MaxSeekTime { get; init; } = TimeSpan.Zero;

        public TimeSpan RawPosition { get; init; } = TimeSpan.Zero;

        public DateTimeOffset LastTimelineSnapshotAtUtc { get; init; } = DateTimeOffset.UtcNow;

        public double PlaybackRate { get; init; } = 1.0;

        public DateTimeOffset LastPlaybackChangedAtUtc { get; init; } = DateTimeOffset.UtcNow;

        public DateTimeOffset LastMetadataChangedAtUtc { get; init; } = DateTimeOffset.UtcNow;

        public DateTimeOffset? LastUserSelectedAtUtc { get; init; }

        public DateTimeOffset LastMeaningfulChangeAtUtc { get; init; } = DateTimeOffset.UtcNow;

        public bool IsEligiblePlaybackState =>
            PlaybackStatus == MediaSessionPlaybackStatus.Playing ||
            PlaybackStatus == MediaSessionPlaybackStatus.Paused;

        public bool IsPlaying => PlaybackStatus == MediaSessionPlaybackStatus.Playing;

        public bool IsPaused => PlaybackStatus == MediaSessionPlaybackStatus.Paused;

        internal string MetadataIdentity =>
            string.Join("|",
                SourceAppUserModelId ?? string.Empty,
                Title ?? string.Empty,
                Artist ?? string.Empty,
                AlbumTitle ?? string.Empty);

        internal bool IsSameLogicalTrack(MediaSessionSnapshot other)
        {
            if (other == null)
            {
                return false;
            }

            return string.Equals(MetadataIdentity, other.MetadataIdentity, StringComparison.OrdinalIgnoreCase);
        }

        internal static MediaSessionPlaybackStatus MapPlaybackStatus(GlobalSystemMediaTransportControlsSessionPlaybackStatus status)
        {
            return status switch
            {
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed => MediaSessionPlaybackStatus.Closed,
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Opened => MediaSessionPlaybackStatus.Stopped,
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Changing => MediaSessionPlaybackStatus.Changing,
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped => MediaSessionPlaybackStatus.Stopped,
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => MediaSessionPlaybackStatus.Playing,
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => MediaSessionPlaybackStatus.Paused,
                _ => MediaSessionPlaybackStatus.Unknown
            };
        }
    }
}
