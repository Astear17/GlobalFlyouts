using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using Windows.Media.Control;

namespace ModernFlyouts.Core.Media.Control
{
    public sealed class MediaSessionService : IDisposable
    {
        private const string SpotifyPackagedAumid = "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify";
        private const string SpotifyUnpackagedAumid = "Spotify.exe";

        private readonly object gate = new();
        private readonly Dictionary<string, GlobalSystemMediaTransportControlsSession> sessionsById = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<GlobalSystemMediaTransportControlsSession, string> sessionIds = new();
        private readonly Dictionary<string, CancellationTokenSource> refreshDebounceTokens = new(StringComparer.OrdinalIgnoreCase);
        private readonly Func<MediaSessionSelectionOptions> getSelectionOptions;
        private readonly ArtworkCache artworkCache;
        private readonly MediaStateStore stateStore = new();

        private GlobalSystemMediaTransportControlsSessionManager sessionManager;
        private int nextSessionNumber;
        private bool disposed;

        public MediaSessionService(Func<MediaSessionSelectionOptions> getSelectionOptions, ArtworkCache artworkCache = null)
        {
            this.getSelectionOptions = getSelectionOptions ?? (() => new MediaSessionSelectionOptions());
            this.artworkCache = artworkCache ?? new ArtworkCache();
            stateStore.SelectionChanged += StateStore_SelectionChanged;
        }

        public MediaSessionSelection CurrentSelection => stateStore.CurrentSelection;

        public async Task InitializeAsync()
        {
            ThrowIfDisposed();

            stateStore.UpdateOptions(getSelectionOptions());

            try
            {
                sessionManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();

                if (sessionManager == null)
                {
                    MediaDiagnostics.Warning("Session manager initialization returned null.");
                    return;
                }

                sessionManager.SessionsChanged += SessionManager_SessionsChanged;
                sessionManager.CurrentSessionChanged += SessionManager_CurrentSessionChanged;

                MediaDiagnostics.Info("Session manager initialized.");
                await RefreshSessionsAsync();
            }
            catch (Exception ex)
            {
                MediaDiagnostics.Exception("Session manager initialization failed", ex);
            }
        }

        public void UpdateSelectionOptions()
        {
            stateStore.UpdateOptions(getSelectionOptions());
        }

        public async Task RefreshSessionsAsync()
        {
            if (sessionManager == null)
            {
                return;
            }

            IReadOnlyList<GlobalSystemMediaTransportControlsSession> sessions;

            try
            {
                sessions = sessionManager.GetSessions();
            }
            catch (Exception ex)
            {
                MediaDiagnostics.Exception("GetSessions failed", ex);
                return;
            }

            var activeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var activeSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            lock (gate)
            {
                foreach (var session in sessions)
                {
                    string id = GetOrCreateStableSessionIdLocked(session);
                    activeIds.Add(id);
                    activeSources.Add(session.SourceAppUserModelId ?? string.Empty);

                    if (!sessionsById.ContainsKey(id))
                    {
                        sessionsById[id] = session;
                        SubscribeSession(session);
                        MediaDiagnostics.Info($"Session added: {id}, SourceAppUserModelId={session.SourceAppUserModelId}");
                    }
                }

                foreach (var pair in sessionsById.ToArray())
                {
                    if (!activeIds.Contains(pair.Key))
                    {
                        UnsubscribeSession(pair.Value);
                        sessionsById.Remove(pair.Key);
                        sessionIds.Remove(pair.Value);
                        stateStore.RemoveSession(pair.Key);
                        MediaDiagnostics.Info($"Session removed: {pair.Key}");
                    }
                }
            }

            stateStore.RetainSessions(activeIds);
            artworkCache.EvictForRemovedSessions(activeSources);

            foreach (var session in sessions)
            {
                string id;
                lock (gate)
                {
                    id = GetOrCreateStableSessionIdLocked(session);
                }

                await RefreshSessionAsync(id, session, "session list refresh");
            }

            SessionsChanged?.Invoke(this, EventArgs.Empty);
        }

        public Task<bool> PlayAsync(string stableSessionId)
        {
            return ExecuteCommandAsync(stableSessionId, async session => await session.TryPlayAsync(), "Play");
        }

        public Task<bool> PauseAsync(string stableSessionId)
        {
            return ExecuteCommandAsync(stableSessionId, async session => await session.TryPauseAsync(), "Pause");
        }

        public Task<bool> TogglePlayPauseAsync(string stableSessionId)
        {
            return ExecuteCommandAsync(stableSessionId, async session => await session.TryTogglePlayPauseAsync(), "TogglePlayPause");
        }

        public Task<bool> NextAsync(string stableSessionId)
        {
            return ExecuteCommandAsync(stableSessionId, async session => await session.TrySkipNextAsync(), "Next");
        }

        public Task<bool> PreviousAsync(string stableSessionId)
        {
            return ExecuteCommandAsync(stableSessionId, async session => await session.TrySkipPreviousAsync(), "Previous");
        }

        public Task<bool> StopAsync(string stableSessionId)
        {
            return ExecuteCommandAsync(stableSessionId, async session => await session.TryStopAsync(), "Stop");
        }

        public Task<bool> SeekToAsync(string stableSessionId, TimeSpan targetPosition)
        {
            return ExecuteCommandAsync(stableSessionId, async session => await session.TryChangePlaybackPositionAsync(targetPosition.Ticks), $"SeekTo {targetPosition}");
        }

        public Task<bool> SetShuffleAsync(string stableSessionId, bool shuffleActive)
        {
            return ExecuteCommandAsync(stableSessionId, async session => await session.TryChangeShuffleActiveAsync(shuffleActive), $"SetShuffle {shuffleActive}");
        }

        public Task<bool> SetRepeatModeAsync(string stableSessionId, MediaPlaybackAutoRepeatMode repeatMode)
        {
            Windows.Media.MediaPlaybackAutoRepeatMode nativeRepeatMode = repeatMode switch
            {
                MediaPlaybackAutoRepeatMode.None => Windows.Media.MediaPlaybackAutoRepeatMode.None,
                MediaPlaybackAutoRepeatMode.Track => Windows.Media.MediaPlaybackAutoRepeatMode.Track,
                MediaPlaybackAutoRepeatMode.List => Windows.Media.MediaPlaybackAutoRepeatMode.List,
                _ => Windows.Media.MediaPlaybackAutoRepeatMode.None
            };

            return ExecuteCommandAsync(stableSessionId, async session => await session.TryChangeAutoRepeatModeAsync(nativeRepeatMode), $"SetRepeat {repeatMode}");
        }

        private async Task<bool> ExecuteCommandAsync(
            string stableSessionId,
            Func<GlobalSystemMediaTransportControlsSession, Task<bool>> command,
            string commandName)
        {
            if (string.IsNullOrWhiteSpace(stableSessionId))
            {
                return false;
            }

            GlobalSystemMediaTransportControlsSession session;

            lock (gate)
            {
                sessionsById.TryGetValue(stableSessionId, out session);
            }

            if (session == null || !stateStore.IsSessionEligible(stableSessionId))
            {
                MediaDiagnostics.Warning($"{commandName} ignored; session is missing or filtered out: {stableSessionId}");
                await RefreshSessionsAsync();
                return false;
            }

            try
            {
                bool result = await command(session);
                MediaDiagnostics.Info($"{commandName} command {(result ? "succeeded" : "was rejected")} for {stableSessionId}");

                if (result)
                {
                    stateStore.MarkUserSelected(stableSessionId);
                }

                ScheduleSessionRefresh(stableSessionId, session, $"{commandName} command result", TimeSpan.FromMilliseconds(50));
                return result;
            }
            catch (Exception ex)
            {
                MediaDiagnostics.Exception($"{commandName} command failed for {stableSessionId}", ex);
                ScheduleSessionRefresh(stableSessionId, session, $"{commandName} command failure", TimeSpan.FromMilliseconds(50));
                return false;
            }
        }

        private void SessionManager_SessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args)
        {
            _ = Task.Run(RefreshSessionsAsync);
        }

        private void SessionManager_CurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
        {
            _ = Task.Run(RefreshSessionsAsync);
        }

        private void Session_MediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
        {
            ScheduleSessionRefresh(sender, "metadata changed");
        }

        private void Session_PlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
        {
            ScheduleSessionRefresh(sender, "playback changed");
        }

        private void Session_TimelinePropertiesChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args)
        {
            ScheduleSessionRefresh(sender, "timeline changed");
        }

        private void ScheduleSessionRefresh(GlobalSystemMediaTransportControlsSession session, string reason)
        {
            string stableSessionId;

            lock (gate)
            {
                stableSessionId = GetOrCreateStableSessionIdLocked(session);
            }

            ScheduleSessionRefresh(stableSessionId, session, reason, TimeSpan.FromMilliseconds(125));
        }

        private void ScheduleSessionRefresh(
            string stableSessionId,
            GlobalSystemMediaTransportControlsSession session,
            string reason,
            TimeSpan delay)
        {
            CancellationTokenSource cts;

            lock (gate)
            {
                if (refreshDebounceTokens.TryGetValue(stableSessionId, out var oldCts))
                {
                    oldCts.Cancel();
                    oldCts.Dispose();
                }

                cts = new CancellationTokenSource();
                refreshDebounceTokens[stableSessionId] = cts;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(delay, cts.Token);
                    await RefreshSessionAsync(stableSessionId, session, reason);
                }
                catch (OperationCanceledException)
                {
                }
                finally
                {
                    lock (gate)
                    {
                        if (refreshDebounceTokens.TryGetValue(stableSessionId, out var current) && current == cts)
                        {
                            refreshDebounceTokens.Remove(stableSessionId);
                        }
                    }

                    cts.Dispose();
                }
            });
        }

        private async Task RefreshSessionAsync(
            string stableSessionId,
            GlobalSystemMediaTransportControlsSession session,
            string reason)
        {
            if (session == null || string.IsNullOrWhiteSpace(stableSessionId))
            {
                return;
            }

            lock (gate)
            {
                if (!sessionsById.ContainsKey(stableSessionId))
                {
                    return;
                }
            }

            try
            {
                var snapshot = await CreateSnapshotAsync(stableSessionId, session);
                stateStore.UpsertSession(snapshot);
                MediaDiagnostics.Trace($"Session snapshot updated ({reason}): {stableSessionId}, {snapshot.PlaybackStatus}, {snapshot.Title}");
            }
            catch (Exception ex)
            {
                MediaDiagnostics.Exception($"Session refresh failed ({reason}) for {stableSessionId}", ex);
            }
        }

        private async Task<MediaSessionSnapshot> CreateSnapshotAsync(
            string stableSessionId,
            GlobalSystemMediaTransportControlsSession session)
        {
            var now = DateTimeOffset.UtcNow;
            var sourceAppUserModelId = session.SourceAppUserModelId ?? string.Empty;

            GlobalSystemMediaTransportControlsSessionPlaybackInfo playback = null;
            GlobalSystemMediaTransportControlsSessionTimelineProperties timeline = null;
            GlobalSystemMediaTransportControlsSessionMediaProperties mediaProperties = null;

            try
            {
                playback = session.GetPlaybackInfo();
            }
            catch (Exception ex)
            {
                MediaDiagnostics.Exception($"GetPlaybackInfo failed for {stableSessionId}", ex);
            }

            try
            {
                timeline = session.GetTimelineProperties();
            }
            catch (Exception ex)
            {
                MediaDiagnostics.Exception($"GetTimelineProperties failed for {stableSessionId}", ex);
            }

            try
            {
                mediaProperties = await session.TryGetMediaPropertiesAsync();
            }
            catch (Exception ex)
            {
                MediaDiagnostics.Exception($"TryGetMediaPropertiesAsync failed for {stableSessionId}", ex);
            }

            var controls = playback?.Controls;
            var canSeek = controls?.IsPlaybackPositionEnabled == true &&
                timeline != null &&
                timeline.EndTime > timeline.StartTime;

            var playbackRate = 1.0;
            var nativePlaybackRate = playback?.PlaybackRate;
            if (nativePlaybackRate.HasValue && nativePlaybackRate.Value > 0)
            {
                playbackRate = nativePlaybackRate.Value;
            }

            var snapshotWithoutArtwork = new MediaSessionSnapshot
            {
                StableSessionId = stableSessionId,
                SourceAppUserModelId = sourceAppUserModelId,
                DisplayAppName = string.IsNullOrWhiteSpace(sourceAppUserModelId) ? stableSessionId : sourceAppUserModelId,
                Title = mediaProperties?.Title ?? string.Empty,
                Artist = mediaProperties?.Artist ?? string.Empty,
                AlbumTitle = mediaProperties?.AlbumTitle ?? string.Empty,
                PlaybackStatus = playback != null ? MediaSessionSnapshot.MapPlaybackStatus(playback.PlaybackStatus) : MediaSessionPlaybackStatus.Unknown,
                PlaybackType = MapPlaybackType(playback),
                CanPlay = controls?.IsPlayEnabled == true,
                CanPause = controls?.IsPauseEnabled == true,
                CanNext = controls?.IsNextEnabled == true,
                CanPrevious = controls?.IsPreviousEnabled == true,
                CanStop = controls?.IsStopEnabled == true,
                CanSeek = canSeek,
                CanChangeShuffle = controls?.IsShuffleEnabled == true,
                CanChangeRepeat = controls?.IsRepeatEnabled == true,
                ShuffleActive = playback?.IsShuffleActive,
                RepeatMode = MapRepeatMode(playback),
                StartTime = timeline?.StartTime ?? TimeSpan.Zero,
                EndTime = timeline?.EndTime ?? TimeSpan.Zero,
                MinSeekTime = timeline?.MinSeekTime ?? timeline?.StartTime ?? TimeSpan.Zero,
                MaxSeekTime = timeline?.MaxSeekTime ?? timeline?.EndTime ?? TimeSpan.Zero,
                RawPosition = timeline?.Position ?? TimeSpan.Zero,
                LastTimelineSnapshotAtUtc = now,
                PlaybackRate = playbackRate,
                LastPlaybackChangedAtUtc = now,
                LastMetadataChangedAtUtc = now,
                LastMeaningfulChangeAtUtc = now
            };

            var artwork = await artworkCache.GetOrDecodeAsync(
                snapshotWithoutArtwork,
                mediaProperties?.Thumbnail,
                bitmap => TransformThumbnail(sourceAppUserModelId, bitmap),
                now);

            return snapshotWithoutArtwork with { ThumbnailImage = artwork };
        }

        private static MediaPlaybackType MapPlaybackType(GlobalSystemMediaTransportControlsSessionPlaybackInfo playback)
        {
            if (playback == null)
            {
                return MediaPlaybackType.Unknown;
            }

            return playback.PlaybackType switch
            {
                Windows.Media.MediaPlaybackType.Music => MediaPlaybackType.Music,
                Windows.Media.MediaPlaybackType.Video => MediaPlaybackType.Video,
                Windows.Media.MediaPlaybackType.Image => MediaPlaybackType.Image,
                _ => MediaPlaybackType.Unknown
            };
        }

        private static MediaPlaybackAutoRepeatMode MapRepeatMode(GlobalSystemMediaTransportControlsSessionPlaybackInfo playback)
        {
            if (playback == null)
            {
                return MediaPlaybackAutoRepeatMode.None;
            }

            return playback.AutoRepeatMode switch
            {
                Windows.Media.MediaPlaybackAutoRepeatMode.Track => MediaPlaybackAutoRepeatMode.Track,
                Windows.Media.MediaPlaybackAutoRepeatMode.List => MediaPlaybackAutoRepeatMode.List,
                _ => MediaPlaybackAutoRepeatMode.None
            };
        }

        private static BitmapSource TransformThumbnail(string sourceAppUserModelId, BitmapSource bitmapSource)
        {
            if (!IsSourceAppSpotify(sourceAppUserModelId) || bitmapSource == null)
            {
                return bitmapSource;
            }

            if (bitmapSource.PixelWidth < 267 || bitmapSource.PixelHeight < 234)
            {
                return bitmapSource;
            }

            BitmapSource croppedBitmap = null;

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                dispatcher.Invoke(() =>
                {
                    croppedBitmap = new CroppedBitmap(bitmapSource, new Int32Rect(33, 0, 234, 234));
                });
            }
            else
            {
                croppedBitmap = new CroppedBitmap(bitmapSource, new Int32Rect(33, 0, 234, 234));
            }

            return croppedBitmap ?? bitmapSource;
        }

        private static bool IsSourceAppSpotify(string sourceAppUserModelId)
        {
            return string.Equals(sourceAppUserModelId, SpotifyPackagedAumid, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(sourceAppUserModelId, SpotifyUnpackagedAumid, StringComparison.OrdinalIgnoreCase);
        }

        private string GetOrCreateStableSessionIdLocked(GlobalSystemMediaTransportControlsSession session)
        {
            if (sessionIds.TryGetValue(session, out string existingId))
            {
                return existingId;
            }

            string source = string.IsNullOrWhiteSpace(session.SourceAppUserModelId)
                ? "unknown"
                : session.SourceAppUserModelId;
            string stableSessionId = $"{source}|{++nextSessionNumber:0000}";
            sessionIds[session] = stableSessionId;
            return stableSessionId;
        }

        private void SubscribeSession(GlobalSystemMediaTransportControlsSession session)
        {
            session.MediaPropertiesChanged += Session_MediaPropertiesChanged;
            session.PlaybackInfoChanged += Session_PlaybackInfoChanged;
            session.TimelinePropertiesChanged += Session_TimelinePropertiesChanged;
        }

        private void UnsubscribeSession(GlobalSystemMediaTransportControlsSession session)
        {
            try
            {
                session.MediaPropertiesChanged -= Session_MediaPropertiesChanged;
                session.PlaybackInfoChanged -= Session_PlaybackInfoChanged;
                session.TimelinePropertiesChanged -= Session_TimelinePropertiesChanged;
            }
            catch
            {
            }
        }

        private void StateStore_SelectionChanged(object sender, MediaSessionSelection selection)
        {
            SelectionChanged?.Invoke(this, selection);
        }

        private void ThrowIfDisposed()
        {
            if (disposed)
            {
                throw new ObjectDisposedException(nameof(MediaSessionService));
            }
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            stateStore.SelectionChanged -= StateStore_SelectionChanged;

            if (sessionManager != null)
            {
                sessionManager.SessionsChanged -= SessionManager_SessionsChanged;
                sessionManager.CurrentSessionChanged -= SessionManager_CurrentSessionChanged;
                sessionManager = null;
            }

            lock (gate)
            {
                foreach (var cts in refreshDebounceTokens.Values)
                {
                    cts.Cancel();
                    cts.Dispose();
                }

                refreshDebounceTokens.Clear();

                foreach (var session in sessionsById.Values)
                {
                    UnsubscribeSession(session);
                }

                sessionsById.Clear();
                sessionIds.Clear();
            }
        }

        public event EventHandler<MediaSessionSelection> SelectionChanged;

        public event EventHandler SessionsChanged;
    }
}
