using Microsoft.Toolkit.Mvvm.Input;
using ModernFlyouts.Core.AppInformation;
using ModernFlyouts.Core.Helpers;
using System;
using System.Windows;
using System.Windows.Threading;

namespace ModernFlyouts.Core.Media.Control
{
    public sealed class EnhancedGSMTCMediaSession : MediaSession
    {
        private readonly MediaSessionService service;
        private readonly TimelineController timelineController = new();
        private readonly Func<bool> isTimelineDisplayActive;
        private readonly DispatcherTimer timelineTimer;
        private MediaSessionSnapshot currentSnapshot;
        private SourceAppInfo sourceAppInfo;
        private string currentSourceAppUserModelId = string.Empty;
        private TimeSpan? pendingScrubPosition;
        private bool disconnected;

        public EnhancedGSMTCMediaSession(
            MediaSessionService service,
            MediaSessionSnapshot snapshot,
            Func<bool> isTimelineDisplayActive)
        {
            this.service = service ?? throw new ArgumentNullException(nameof(service));
            this.isTimelineDisplayActive = isTimelineDisplayActive ?? (() => false);
            timelineTimer = new DispatcherTimer(DispatcherPriority.Background, Application.Current.Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            timelineTimer.Tick += TimelineTimer_Tick;

            ApplySnapshot(snapshot);
        }

        public string StableSessionId => currentSnapshot?.StableSessionId ?? string.Empty;

        public void RefreshTimelineActivity()
        {
            UpdateTimelineTimer();
        }

        public void BeginTimelineScrub()
        {
            timelineController.BeginDrag();
            pendingScrubPosition = null;
        }

        public void EndTimelineScrub()
        {
            timelineController.EndDrag();

            if (pendingScrubPosition.HasValue)
            {
                CommitSeek(pendingScrubPosition.Value);
                pendingScrubPosition = null;
            }
        }

        public void ApplySnapshot(MediaSessionSnapshot snapshot)
        {
            if (snapshot == null || disconnected)
            {
                return;
            }

            bool metadataChanged = currentSnapshot == null ||
                !string.Equals(currentSnapshot.MetadataIdentity, snapshot.MetadataIdentity, StringComparison.OrdinalIgnoreCase);
            bool timelineReset = metadataChanged ||
                currentSnapshot == null ||
                currentSnapshot.StartTime != snapshot.StartTime ||
                currentSnapshot.EndTime != snapshot.EndTime ||
                !string.Equals(currentSnapshot.SourceAppUserModelId, snapshot.SourceAppUserModelId, StringComparison.OrdinalIgnoreCase);

            if (metadataChanged)
            {
                RaiseMediaPropertiesChanging();
            }

            currentSnapshot = snapshot;

            if (timelineReset)
            {
                timelineController.Reset(snapshot);
            }

            Title = snapshot.Title ?? string.Empty;
            Artist = snapshot.Artist ?? string.Empty;

            if (snapshot.ThumbnailImage != null)
            {
                Thumbnail = snapshot.ThumbnailImage;
            }

            PlaybackType = snapshot.PlaybackType;
            IsPlaying = snapshot.PlaybackStatus == MediaSessionPlaybackStatus.Playing;
            IsPlayEnabled = snapshot.CanPlay;
            IsPauseEnabled = snapshot.CanPause;
            IsPlayOrPauseEnabled = snapshot.CanTogglePlayPause;
            IsPreviousEnabled = snapshot.CanPrevious;
            IsNextEnabled = snapshot.CanNext;
            IsStopEnabled = snapshot.CanStop;
            IsShuffleEnabled = snapshot.CanChangeShuffle;
            IsRepeatEnabled = snapshot.CanChangeRepeat;
            IsPlaybackPositionEnabled = snapshot.CanSeek;
            IsTimelinePropertiesEnabled = snapshot.CanSeek && snapshot.EndTime > snapshot.StartTime;
            IsShuffleActive = snapshot.ShuffleActive;
            AutoRepeatMode = snapshot.RepeatMode;
            TimelineStartTime = snapshot.StartTime;
            TimelineEndTime = snapshot.EndTime;
            SetPlaybackPosition(timelineController.GetDisplayPosition(snapshot, DateTimeOffset.UtcNow));

            UpdateSourceAppInfo(snapshot);
            UpdateTimelineTimer();

            if (metadataChanged)
            {
                RaiseMediaPropertiesChanged();
            }
        }

        public override void Disconnect()
        {
            disconnected = true;
            timelineTimer.Stop();
            timelineTimer.Tick -= TimelineTimer_Tick;

            if (sourceAppInfo != null)
            {
                sourceAppInfo.InfoFetched -= SourceAppInfo_InfoFetched;
                sourceAppInfo.Dispose();
                sourceAppInfo = null;
            }
        }

        protected override async void Play()
        {
            await service.PlayAsync(StableSessionId);
        }

        protected override async void Pause()
        {
            await service.PauseAsync(StableSessionId);
        }

        protected override async void PreviousTrack()
        {
            await service.PreviousAsync(StableSessionId);
        }

        protected override async void NextTrack()
        {
            await service.NextAsync(StableSessionId);
        }

        protected override async void ChangeShuffleActive()
        {
            if (IsShuffleActive.HasValue)
            {
                await service.SetShuffleAsync(StableSessionId, !IsShuffleActive.Value);
            }
        }

        protected override async void ChangeAutoRepeatMode()
        {
            var repeatMode = AutoRepeatMode switch
            {
                MediaPlaybackAutoRepeatMode.None => MediaPlaybackAutoRepeatMode.List,
                MediaPlaybackAutoRepeatMode.List => MediaPlaybackAutoRepeatMode.Track,
                MediaPlaybackAutoRepeatMode.Track => MediaPlaybackAutoRepeatMode.None,
                _ => MediaPlaybackAutoRepeatMode.None
            };

            await service.SetRepeatModeAsync(StableSessionId, repeatMode);
        }

        protected override async void Stop()
        {
            await service.StopAsync(StableSessionId);
        }

        protected override async void PlaybackPositionChanged(TimeSpan playbackPosition)
        {
            if (currentSnapshot == null || !currentSnapshot.CanSeek)
            {
                return;
            }

            if (timelineController.IsDragging)
            {
                pendingScrubPosition = playbackPosition;
                return;
            }

            await CommitSeekAsync(playbackPosition);
        }

        private async void CommitSeek(TimeSpan playbackPosition)
        {
            await CommitSeekAsync(playbackPosition);
        }

        private async System.Threading.Tasks.Task CommitSeekAsync(TimeSpan playbackPosition)
        {
            var clampedPosition = TimelineController.Clamp(playbackPosition, currentSnapshot);
            timelineController.BeginOptimisticSeek(currentSnapshot, clampedPosition, DateTimeOffset.UtcNow);
            SetPlaybackPosition(clampedPosition);

            bool accepted = await service.SeekToAsync(StableSessionId, clampedPosition);
            timelineController.CompleteSeek(accepted);

            if (!accepted)
            {
                SetPlaybackPosition(timelineController.GetDisplayPosition(currentSnapshot, DateTimeOffset.UtcNow));
            }
        }

        private void TimelineTimer_Tick(object sender, EventArgs e)
        {
            if (!ShouldRunTimelineTimer())
            {
                timelineTimer.Stop();
                return;
            }

            if (timelineController.TryReconcile(currentSnapshot, DateTimeOffset.UtcNow, out var position))
            {
                SetPlaybackPosition(position);
            }
            else
            {
                SetPlaybackPosition(position);
            }
        }

        private void UpdateTimelineTimer()
        {
            if (ShouldRunTimelineTimer())
            {
                if (!timelineTimer.IsEnabled)
                {
                    timelineTimer.Start();
                }
            }
            else
            {
                timelineTimer.Stop();
            }
        }

        private bool ShouldRunTimelineTimer()
        {
            return currentSnapshot?.CanSeek == true &&
                currentSnapshot.PlaybackStatus == MediaSessionPlaybackStatus.Playing &&
                isTimelineDisplayActive();
        }

        private void UpdateSourceAppInfo(MediaSessionSnapshot snapshot)
        {
            if (string.Equals(currentSourceAppUserModelId, snapshot.SourceAppUserModelId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            currentSourceAppUserModelId = snapshot.SourceAppUserModelId ?? string.Empty;

            if (sourceAppInfo != null)
            {
                sourceAppInfo.InfoFetched -= SourceAppInfo_InfoFetched;
                sourceAppInfo.Dispose();
                sourceAppInfo = null;
            }

            MediaSourceName = string.IsNullOrWhiteSpace(snapshot.DisplayAppName)
                ? snapshot.SourceAppUserModelId
                : snapshot.DisplayAppName;
            MediaSourceIcon = null;

            sourceAppInfo = SourceAppInfo.FromAppUserModelId(currentSourceAppUserModelId);
            if (sourceAppInfo == null)
            {
                ActivateMediaSourceCommand = null;
                return;
            }

            ActivateMediaSourceCommand = new RelayCommand(() => sourceAppInfo?.Activate(), () => sourceAppInfo != null);
            sourceAppInfo.InfoFetched += SourceAppInfo_InfoFetched;
            sourceAppInfo.FetchInfosAsync();
        }

        private void SourceAppInfo_InfoFetched(object sender, EventArgs e)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
            {
                ApplySourceAppInfo();
            }
            else
            {
                dispatcher.BeginInvoke(new Action(ApplySourceAppInfo));
            }
        }

        private void ApplySourceAppInfo()
        {
            if (sourceAppInfo == null)
            {
                return;
            }

            sourceAppInfo.InfoFetched -= SourceAppInfo_InfoFetched;

            if (!string.IsNullOrWhiteSpace(sourceAppInfo.DisplayName))
            {
                MediaSourceName = sourceAppInfo.DisplayName;
            }

            if (BitmapHelper.TryCreateBitmapImageFromStream(sourceAppInfo.LogoStream, out var bitmap))
            {
                MediaSourceIcon = bitmap;
            }
        }
    }
}
