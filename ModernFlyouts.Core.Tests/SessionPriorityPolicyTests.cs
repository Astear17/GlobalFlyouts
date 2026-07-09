using ModernFlyouts.Core.Media.Control;
using System;
using Xunit;

namespace ModernFlyouts.Core.Tests
{
    public class SessionPriorityPolicyTests
    {
        [Fact]
        public void PlayingSessionBeatsPausedSession()
        {
            var selection = Select(
                Snapshot("ytm", "YouTube Music", MediaSessionPlaybackStatus.Playing, 2),
                Snapshot("spotify", "Spotify", MediaSessionPlaybackStatus.Paused, 3));

            Assert.Equal("ytm", selection.Snapshot.SourceAppUserModelId);
        }

        [Fact]
        public void StalePausedBrowserDoesNotOverridePlayingDesktopApp()
        {
            var selection = Select(
                Snapshot("browser", "Browser", MediaSessionPlaybackStatus.Paused, 100),
                Snapshot("spotify", "Spotify", MediaSessionPlaybackStatus.Playing, 1));

            Assert.Equal("spotify", selection.Snapshot.SourceAppUserModelId);
        }

        [Fact]
        public void MostRecentlyChangedPlayingSessionWinsDeterministically()
        {
            var selection = Select(
                Snapshot("browser-a", "Browser", MediaSessionPlaybackStatus.Playing, 1),
                Snapshot("browser-b", "Browser", MediaSessionPlaybackStatus.Playing, 5));

            Assert.Equal("browser-b", selection.Snapshot.SourceAppUserModelId);
        }

        [Fact]
        public void PinnedPausedAppDoesNotOverridePlayingAppByDefault()
        {
            var options = Options(pinned: "ytm");

            var selection = Select(options,
                Snapshot("ytm", "YouTube Music", MediaSessionPlaybackStatus.Paused, 10),
                Snapshot("spotify", "Spotify", MediaSessionPlaybackStatus.Playing, 1));

            Assert.Equal("spotify", selection.Snapshot.SourceAppUserModelId);
        }

        [Fact]
        public void PinnedPausedAppCanOverrideWhenExplicitlyEnabled()
        {
            var options = Options("ytm", PinnedAppPriorityMode.AlwaysPreferPinnedIfEligible);

            var selection = Select(options,
                Snapshot("ytm", "YouTube Music", MediaSessionPlaybackStatus.Paused, 1),
                Snapshot("spotify", "Spotify", MediaSessionPlaybackStatus.Playing, 10));

            Assert.Equal("ytm", selection.Snapshot.SourceAppUserModelId);
        }

        [Fact]
        public void PinnedPlayingAppBeatsOtherPlayingApps()
        {
            var options = Options(pinned: "ytm");

            var selection = Select(options,
                Snapshot("ytm", "YouTube Music", MediaSessionPlaybackStatus.Playing, 1),
                Snapshot("spotify", "Spotify", MediaSessionPlaybackStatus.Playing, 10));

            Assert.Equal("ytm", selection.Snapshot.SourceAppUserModelId);
        }

        [Fact]
        public void BlockedPlayingAppIsIgnoredBeforePriority()
        {
            var options = new MediaSessionSelectionOptions
            {
                AppFilteringMode = MediaAppFilteringMode.Blocklist,
                AppFilterEntries = new[] { "blocked" }
            };

            var selection = Select(options,
                Snapshot("blocked", "Blocked", MediaSessionPlaybackStatus.Playing, 10),
                Snapshot("allowed", "Allowed", MediaSessionPlaybackStatus.Paused, 1));

            Assert.Equal("allowed", selection.Snapshot.SourceAppUserModelId);
        }

        [Fact]
        public void StoppedSessionsAreNotDisplayed()
        {
            var selection = Select(Snapshot("stopped", "Stopped", MediaSessionPlaybackStatus.Stopped, 10));

            Assert.Null(selection.Snapshot);
        }

        private static MediaSessionSelection Select(params MediaSessionSnapshot[] snapshots)
        {
            return Select(new MediaSessionSelectionOptions(), snapshots);
        }

        private static MediaSessionSelection Select(MediaSessionSelectionOptions options, params MediaSessionSnapshot[] snapshots)
        {
            var store = new MediaStateStore();
            return store.SelectSessionForTesting(snapshots, options);
        }

        private static MediaSessionSelectionOptions Options(
            string pinned = "",
            PinnedAppPriorityMode pinnedMode = PinnedAppPriorityMode.PreferPinnedOnlyWhenPlaying)
        {
            return new MediaSessionSelectionOptions
            {
                PinnedAppUserModelId = pinned,
                PinnedAppPriorityMode = pinnedMode
            };
        }

        private static MediaSessionSnapshot Snapshot(
            string sourceAppUserModelId,
            string displayName,
            MediaSessionPlaybackStatus status,
            int changedAtSeconds)
        {
            return new MediaSessionSnapshot
            {
                StableSessionId = sourceAppUserModelId,
                SourceAppUserModelId = sourceAppUserModelId,
                DisplayAppName = displayName,
                PlaybackStatus = status,
                LastMeaningfulChangeAtUtc = DateTimeOffset.UnixEpoch.AddSeconds(changedAtSeconds)
            };
        }
    }
}
