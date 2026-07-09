using System;

namespace ModernFlyouts.Core.Media.Control
{
    public sealed class TimelineController
    {
        private readonly object gate = new();
        private PendingSeek pendingSeek;
        private MediaSessionSnapshot lastKnownSnapshot;

        public TimeSpan ReconciliationTimeout { get; init; } = TimeSpan.FromSeconds(2);

        public TimeSpan ConvergenceTolerance { get; init; } = TimeSpan.FromSeconds(2);

        public bool IsDragging { get; private set; }

        public void Reset(MediaSessionSnapshot snapshot)
        {
            lock (gate)
            {
                lastKnownSnapshot = snapshot;
                pendingSeek = null;
                IsDragging = false;
            }
        }

        public void BeginDrag()
        {
            lock (gate)
            {
                IsDragging = true;
            }
        }

        public void EndDrag()
        {
            lock (gate)
            {
                IsDragging = false;
            }
        }

        public TimeSpan GetDisplayPosition(MediaSessionSnapshot snapshot, DateTimeOffset utcNow)
        {
            lock (gate)
            {
                if (snapshot == null)
                {
                    return TimeSpan.Zero;
                }

                if (!IsDragging)
                {
                    lastKnownSnapshot = snapshot;
                }

                if (pendingSeek != null && pendingSeek.ResultAccepted)
                {
                    return Clamp(pendingSeek.TargetPosition, snapshot);
                }

                if (IsDragging)
                {
                    return Clamp(snapshot.RawPosition, snapshot);
                }

                return CalculateDisplayPosition(snapshot, utcNow);
            }
        }

        public void BeginOptimisticSeek(MediaSessionSnapshot snapshot, TimeSpan targetPosition, DateTimeOffset utcNow)
        {
            lock (gate)
            {
                lastKnownSnapshot = snapshot;
                pendingSeek = new PendingSeek
                {
                    StartedAtUtc = utcNow,
                    TargetPosition = Clamp(targetPosition, snapshot),
                    LastKnownGoodSnapshot = snapshot,
                    ResultAccepted = true
                };
            }
        }

        public void CompleteSeek(bool accepted)
        {
            lock (gate)
            {
                if (pendingSeek == null)
                {
                    return;
                }

                if (!accepted)
                {
                    pendingSeek = null;
                    return;
                }

                pendingSeek.ResultAccepted = true;
            }
        }

        public bool TryReconcile(MediaSessionSnapshot snapshot, DateTimeOffset utcNow, out TimeSpan position)
        {
            lock (gate)
            {
                position = snapshot?.RawPosition ?? TimeSpan.Zero;

                if (pendingSeek == null || snapshot == null)
                {
                    return true;
                }

                var reportedPosition = CalculateDisplayPosition(snapshot, utcNow);
                var distance = (reportedPosition - pendingSeek.TargetPosition).Duration();

                if (distance <= ConvergenceTolerance)
                {
                    pendingSeek = null;
                    position = reportedPosition;
                    return true;
                }

                if (utcNow - pendingSeek.StartedAtUtc >= ReconciliationTimeout)
                {
                    var revertSnapshot = pendingSeek.LastKnownGoodSnapshot ?? lastKnownSnapshot ?? snapshot;
                    position = CalculateDisplayPosition(revertSnapshot, utcNow);
                    pendingSeek = null;
                    MediaDiagnostics.Warning($"Seek reconciliation reverted for {snapshot.StableSessionId}; reported position did not converge.");
                    return false;
                }

                position = pendingSeek.TargetPosition;
                return true;
            }
        }

        public static TimeSpan CalculateDisplayPosition(MediaSessionSnapshot snapshot, DateTimeOffset utcNow)
        {
            if (snapshot == null)
            {
                return TimeSpan.Zero;
            }

            TimeSpan position = snapshot.RawPosition;

            if (snapshot.PlaybackStatus == MediaSessionPlaybackStatus.Playing)
            {
                double playbackRate = snapshot.PlaybackRate > 0 ? snapshot.PlaybackRate : 1.0;
                TimeSpan elapsed = utcNow - snapshot.LastTimelineSnapshotAtUtc;
                if (elapsed > TimeSpan.Zero)
                {
                    position += TimeSpan.FromTicks((long)(elapsed.Ticks * playbackRate));
                }
            }

            return Clamp(position, snapshot);
        }

        public static TimeSpan Clamp(TimeSpan position, MediaSessionSnapshot snapshot)
        {
            if (snapshot == null)
            {
                return position;
            }

            TimeSpan min = snapshot.MinSeekTime > TimeSpan.Zero ? snapshot.MinSeekTime : snapshot.StartTime;
            TimeSpan max = snapshot.MaxSeekTime > TimeSpan.Zero ? snapshot.MaxSeekTime : snapshot.EndTime;

            if (max <= min)
            {
                max = snapshot.EndTime > snapshot.StartTime ? snapshot.EndTime : TimeSpan.MaxValue;
            }

            if (position < min)
            {
                return min;
            }

            if (position > max)
            {
                return max;
            }

            return position;
        }

        private sealed class PendingSeek
        {
            public DateTimeOffset StartedAtUtc { get; init; }

            public TimeSpan TargetPosition { get; init; }

            public MediaSessionSnapshot LastKnownGoodSnapshot { get; init; }

            public bool ResultAccepted { get; set; }
        }
    }
}
