using Microsoft.Toolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace ModernFlyouts.Core.Media.Control
{
    public sealed class MediaStateStore : ObservableObject
    {
        private readonly object gate = new();
        private readonly Dictionary<string, MediaSessionSnapshot> sessions = new(StringComparer.OrdinalIgnoreCase);
        private static readonly TimeSpan TimelineTickTolerance = TimeSpan.FromSeconds(2);
        private MediaSessionSelectionOptions options = new();
        private MediaSessionSelection currentSelection = MediaSessionSelection.Empty;

        public MediaSessionSelection CurrentSelection
        {
            get => currentSelection;
            private set
            {
                if (!Equals(currentSelection, value))
                {
                    SetProperty(ref currentSelection, value);
                    SelectionChanged?.Invoke(this, value);
                }
            }
        }

        public IReadOnlyList<MediaSessionSnapshot> GetSessionsSnapshot()
        {
            lock (gate)
            {
                return sessions.Values.ToArray();
            }
        }

        public void UpdateOptions(MediaSessionSelectionOptions selectionOptions)
        {
            lock (gate)
            {
                options = selectionOptions ?? new MediaSessionSelectionOptions();
                RecalculateSelectionLocked();
            }
        }

        public void UpsertSession(MediaSessionSnapshot snapshot, DateTimeOffset? utcNow = null)
        {
            if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.StableSessionId))
            {
                return;
            }

            lock (gate)
            {
                var now = utcNow ?? DateTimeOffset.UtcNow;
                sessions.TryGetValue(snapshot.StableSessionId, out var previous);
                sessions[snapshot.StableSessionId] = NormalizeTimestamps(snapshot, previous, now);
                RecalculateSelectionLocked();
            }
        }

        public void RemoveSession(string stableSessionId)
        {
            if (string.IsNullOrWhiteSpace(stableSessionId))
            {
                return;
            }

            lock (gate)
            {
                sessions.Remove(stableSessionId);
                RecalculateSelectionLocked();
            }
        }

        public void RetainSessions(IEnumerable<string> stableSessionIds)
        {
            var retained = new HashSet<string>(stableSessionIds ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);

            lock (gate)
            {
                foreach (string sessionId in sessions.Keys.ToArray())
                {
                    if (!retained.Contains(sessionId))
                    {
                        sessions.Remove(sessionId);
                    }
                }

                RecalculateSelectionLocked();
            }
        }

        public bool IsSessionEligible(string stableSessionId)
        {
            lock (gate)
            {
                return sessions.TryGetValue(stableSessionId, out var snapshot) && IsEligible(snapshot);
            }
        }

        public bool TryGetSession(string stableSessionId, out MediaSessionSnapshot snapshot)
        {
            lock (gate)
            {
                return sessions.TryGetValue(stableSessionId, out snapshot);
            }
        }

        public void MarkUserSelected(string stableSessionId, DateTimeOffset? utcNow = null)
        {
            lock (gate)
            {
                if (sessions.TryGetValue(stableSessionId, out var snapshot))
                {
                    var now = utcNow ?? DateTimeOffset.UtcNow;
                    sessions[stableSessionId] = snapshot with
                    {
                        LastUserSelectedAtUtc = now,
                        LastMeaningfulChangeAtUtc = now
                    };
                    RecalculateSelectionLocked();
                }
            }
        }

        public MediaSessionSelection SelectSessionForTesting(IEnumerable<MediaSessionSnapshot> snapshots, MediaSessionSelectionOptions selectionOptions)
        {
            lock (gate)
            {
                sessions.Clear();
                options = selectionOptions ?? new MediaSessionSelectionOptions();

                foreach (var snapshot in snapshots ?? Array.Empty<MediaSessionSnapshot>())
                {
                    if (!string.IsNullOrWhiteSpace(snapshot.StableSessionId))
                    {
                        sessions[snapshot.StableSessionId] = snapshot;
                    }
                }

                RecalculateSelectionLocked();
                return CurrentSelection;
            }
        }

        private MediaSessionSnapshot NormalizeTimestamps(MediaSessionSnapshot snapshot, MediaSessionSnapshot previous, DateTimeOffset utcNow)
        {
            if (previous == null)
            {
                return snapshot with
                {
                    LastPlaybackChangedAtUtc = snapshot.LastPlaybackChangedAtUtc == default ? utcNow : snapshot.LastPlaybackChangedAtUtc,
                    LastMetadataChangedAtUtc = snapshot.LastMetadataChangedAtUtc == default ? utcNow : snapshot.LastMetadataChangedAtUtc,
                    LastTimelineSnapshotAtUtc = snapshot.LastTimelineSnapshotAtUtc == default ? utcNow : snapshot.LastTimelineSnapshotAtUtc,
                    LastMeaningfulChangeAtUtc = snapshot.LastMeaningfulChangeAtUtc == default ? utcNow : snapshot.LastMeaningfulChangeAtUtc
                };
            }

            bool playbackChanged = previous.PlaybackStatus != snapshot.PlaybackStatus;
            bool metadataChanged = !previous.IsSameLogicalTrack(snapshot);
            bool timelineRangeChanged =
                previous.StartTime != snapshot.StartTime ||
                previous.EndTime != snapshot.EndTime ||
                previous.MinSeekTime != snapshot.MinSeekTime ||
                previous.MaxSeekTime != snapshot.MaxSeekTime;

            var playbackChangedAt = playbackChanged ? utcNow : previous.LastPlaybackChangedAtUtc;
            var metadataChangedAt = metadataChanged ? utcNow : previous.LastMetadataChangedAtUtc;
            var meaningfulChangeAt = playbackChanged || metadataChanged || timelineRangeChanged
                ? utcNow
                : previous.LastMeaningfulChangeAtUtc;

            return snapshot with
            {
                LastPlaybackChangedAtUtc = playbackChangedAt,
                LastMetadataChangedAtUtc = metadataChangedAt,
                LastUserSelectedAtUtc = previous.LastUserSelectedAtUtc,
                LastMeaningfulChangeAtUtc = meaningfulChangeAt
            };
        }

        private void RecalculateSelectionLocked()
        {
            var selection = SelectSessionLocked();

            if (!string.Equals(selection.Snapshot?.StableSessionId, CurrentSelection.Snapshot?.StableSessionId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(selection.Reason, CurrentSelection.Reason, StringComparison.Ordinal))
            {
                MediaDiagnostics.Info("Session priority decision: " + DescribeSelection(selection));
            }

            if (ShouldPublishSelectionChange(CurrentSelection, selection))
            {
                CurrentSelection = selection;
            }
            else
            {
                currentSelection = selection;
            }
        }

        private static bool ShouldPublishSelectionChange(MediaSessionSelection previous, MediaSessionSelection next)
        {
            if (!string.Equals(previous?.Snapshot?.StableSessionId, next?.Snapshot?.StableSessionId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(previous?.Reason, next?.Reason, StringComparison.Ordinal))
            {
                return true;
            }

            if (previous?.Snapshot == null || next?.Snapshot == null)
            {
                return false;
            }

            return !IsExpectedPlayingTimelineTick(previous.Snapshot, next.Snapshot);
        }

        private static bool IsExpectedPlayingTimelineTick(MediaSessionSnapshot previous, MediaSessionSnapshot next)
        {
            if (next.PlaybackStatus != MediaSessionPlaybackStatus.Playing ||
                !HasSameNonTimelineState(previous, next))
            {
                return false;
            }

            var expectedPosition = TimelineController.CalculateDisplayPosition(previous, next.LastTimelineSnapshotAtUtc);
            return (expectedPosition - next.RawPosition).Duration() <= TimelineTickTolerance;
        }

        private static bool HasSameNonTimelineState(MediaSessionSnapshot previous, MediaSessionSnapshot next)
        {
            return string.Equals(previous.StableSessionId, next.StableSessionId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(previous.SourceAppUserModelId, next.SourceAppUserModelId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(previous.DisplayAppName, next.DisplayAppName, StringComparison.Ordinal) &&
                string.Equals(previous.Title, next.Title, StringComparison.Ordinal) &&
                string.Equals(previous.Artist, next.Artist, StringComparison.Ordinal) &&
                string.Equals(previous.AlbumTitle, next.AlbumTitle, StringComparison.Ordinal) &&
                Equals(previous.ThumbnailImage, next.ThumbnailImage) &&
                previous.PlaybackStatus == next.PlaybackStatus &&
                previous.PlaybackType == next.PlaybackType &&
                previous.CanPlay == next.CanPlay &&
                previous.CanPause == next.CanPause &&
                previous.CanNext == next.CanNext &&
                previous.CanPrevious == next.CanPrevious &&
                previous.CanStop == next.CanStop &&
                previous.CanSeek == next.CanSeek &&
                previous.CanChangeShuffle == next.CanChangeShuffle &&
                previous.CanChangeRepeat == next.CanChangeRepeat &&
                previous.ShuffleActive == next.ShuffleActive &&
                previous.RepeatMode == next.RepeatMode &&
                previous.StartTime == next.StartTime &&
                previous.EndTime == next.EndTime &&
                previous.MinSeekTime == next.MinSeekTime &&
                previous.MaxSeekTime == next.MaxSeekTime &&
                Math.Abs(previous.PlaybackRate - next.PlaybackRate) < double.Epsilon;
        }

        private MediaSessionSelection SelectSessionLocked()
        {
            var eligible = sessions.Values
                .Where(IsEligible)
                .ToArray();

            if (eligible.Length == 0)
            {
                var blockedCount = sessions.Count - sessions.Values.Count(x => x.IsEligiblePlaybackState);
                return new MediaSessionSelection(null, $"No eligible media session (filtered/stopped count: {blockedCount})");
            }

            var pinned = eligible
                .Where(IsPinned)
                .ToArray();

            if (options.PinnedAppPriorityMode == PinnedAppPriorityMode.AlwaysPreferPinnedIfEligible)
            {
                var pinnedBest = ChooseBest(pinned);
                if (pinnedBest != null)
                {
                    return new MediaSessionSelection(pinnedBest, "Pinned app selected because AlwaysPreferPinnedIfEligible is enabled");
                }
            }

            var pinnedPlaying = ChooseBest(pinned.Where(x => x.IsPlaying));
            if (pinnedPlaying != null)
            {
                return new MediaSessionSelection(pinnedPlaying, "Pinned app has a playing eligible session");
            }

            var playing = ChooseBest(eligible.Where(x => x.IsPlaying));
            if (playing != null)
            {
                return new MediaSessionSelection(playing, "Most recently changed playing session");
            }

            var paused = ChooseBest(eligible.Where(x => x.IsPaused));
            if (paused != null)
            {
                return new MediaSessionSelection(paused, IsPinned(paused)
                    ? "Pinned paused session selected because no eligible session is playing"
                    : "Most recently changed paused session");
            }

            return new MediaSessionSelection(null, "No playing or paused media session");
        }

        private MediaSessionSnapshot ChooseBest(IEnumerable<MediaSessionSnapshot> candidates)
        {
            return candidates
                .OrderByDescending(x => x.PlaybackStatus == MediaSessionPlaybackStatus.Playing)
                .ThenByDescending(x => x.LastMeaningfulChangeAtUtc)
                .ThenByDescending(x => x.LastUserSelectedAtUtc ?? DateTimeOffset.MinValue)
                .ThenBy(x => x.StableSessionId, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }

        private bool IsEligible(MediaSessionSnapshot snapshot)
        {
            return snapshot != null &&
                snapshot.IsEligiblePlaybackState &&
                IsAllowedByFilter(snapshot);
        }

        private bool IsPinned(MediaSessionSnapshot snapshot)
        {
            return MatchesEntry(snapshot, options.PinnedAppUserModelId);
        }

        private bool IsAllowedByFilter(MediaSessionSnapshot snapshot)
        {
            if (options.AppFilteringMode == MediaAppFilteringMode.Disabled ||
                options.AppFilterEntries == null ||
                options.AppFilterEntries.Count == 0)
            {
                return true;
            }

            bool matches = options.AppFilterEntries.Any(entry => MatchesEntry(snapshot, entry));

            return options.AppFilteringMode switch
            {
                MediaAppFilteringMode.Allowlist => matches,
                MediaAppFilteringMode.Blocklist => !matches,
                _ => true
            };
        }

        private static bool MatchesEntry(MediaSessionSnapshot snapshot, string entry)
        {
            if (snapshot == null || string.IsNullOrWhiteSpace(entry))
            {
                return false;
            }

            return string.Equals(snapshot.SourceAppUserModelId, entry, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(snapshot.DisplayAppName, entry, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(snapshot.SourceAppUserModelId) &&
                    snapshot.SourceAppUserModelId.IndexOf(entry, StringComparison.OrdinalIgnoreCase) >= 0) ||
                (!string.IsNullOrWhiteSpace(snapshot.DisplayAppName) &&
                    snapshot.DisplayAppName.IndexOf(entry, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static string DescribeSelection(MediaSessionSelection selection)
        {
            if (selection?.Snapshot == null)
            {
                return selection?.Reason ?? "No eligible media session";
            }

            return $"{selection.Snapshot.StableSessionId} ({selection.Snapshot.SourceAppUserModelId}) - {selection.Reason}";
        }

        public event EventHandler<MediaSessionSelection> SelectionChanged;
    }
}
