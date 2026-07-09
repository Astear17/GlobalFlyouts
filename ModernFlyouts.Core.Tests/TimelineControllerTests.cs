using ModernFlyouts.Core.Media.Control;
using System;
using Xunit;

namespace ModernFlyouts.Core.Tests
{
    public class TimelineControllerTests
    {
        [Fact]
        public void PlayingSessionExtrapolatesPosition()
        {
            var now = DateTimeOffset.UnixEpoch.AddSeconds(10);
            var snapshot = Snapshot(MediaSessionPlaybackStatus.Playing, TimeSpan.FromSeconds(20), now);

            var position = TimelineController.CalculateDisplayPosition(snapshot, now.AddSeconds(5));

            Assert.Equal(TimeSpan.FromSeconds(25), position);
        }

        [Fact]
        public void PausedSessionDoesNotExtrapolate()
        {
            var now = DateTimeOffset.UnixEpoch.AddSeconds(10);
            var snapshot = Snapshot(MediaSessionPlaybackStatus.Paused, TimeSpan.FromSeconds(20), now);

            var position = TimelineController.CalculateDisplayPosition(snapshot, now.AddSeconds(5));

            Assert.Equal(TimeSpan.FromSeconds(20), position);
        }

        [Fact]
        public void PositionIsClampedToEndTime()
        {
            var now = DateTimeOffset.UnixEpoch.AddSeconds(10);
            var snapshot = Snapshot(MediaSessionPlaybackStatus.Playing, TimeSpan.FromSeconds(98), now);

            var position = TimelineController.CalculateDisplayPosition(snapshot, now.AddSeconds(10));

            Assert.Equal(TimeSpan.FromSeconds(100), position);
        }

        [Fact]
        public void FailedSeekRevertsImmediatelyToLastSnapshot()
        {
            var now = DateTimeOffset.UnixEpoch;
            var snapshot = Snapshot(MediaSessionPlaybackStatus.Paused, TimeSpan.FromSeconds(10), now);
            var controller = new TimelineController();

            controller.BeginOptimisticSeek(snapshot, TimeSpan.FromSeconds(50), now);
            controller.CompleteSeek(false);

            Assert.Equal(TimeSpan.FromSeconds(10), controller.GetDisplayPosition(snapshot, now));
        }

        [Fact]
        public void AcceptedSeekRevertsWhenReportedPositionDoesNotConverge()
        {
            var now = DateTimeOffset.UnixEpoch;
            var snapshot = Snapshot(MediaSessionPlaybackStatus.Paused, TimeSpan.FromSeconds(10), now);
            var controller = new TimelineController();

            controller.BeginOptimisticSeek(snapshot, TimeSpan.FromSeconds(80), now);
            controller.CompleteSeek(true);

            bool converged = controller.TryReconcile(snapshot, now.AddSeconds(3), out var position);

            Assert.False(converged);
            Assert.Equal(TimeSpan.FromSeconds(10), position);
        }

        private static MediaSessionSnapshot Snapshot(MediaSessionPlaybackStatus status, TimeSpan position, DateTimeOffset capturedAt)
        {
            return new MediaSessionSnapshot
            {
                StableSessionId = "session",
                PlaybackStatus = status,
                RawPosition = position,
                StartTime = TimeSpan.Zero,
                EndTime = TimeSpan.FromSeconds(100),
                MinSeekTime = TimeSpan.Zero,
                MaxSeekTime = TimeSpan.FromSeconds(100),
                LastTimelineSnapshotAtUtc = capturedAt,
                PlaybackRate = 1.0
            };
        }
    }
}
