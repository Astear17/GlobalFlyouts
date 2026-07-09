# Enhanced Media Backend Notes

## Architecture

- `MediaControlPreflight` runs inside the app runtime before the enhanced GSMTC backend is initialized. It checks type visibility, `RequestAsync`, manager null, `GetSessions`, and basic session property reads. Zero sessions is treated as API available.
- `MediaSessionService` owns `GlobalSystemMediaTransportControlsSessionManager`, subscribes to session and per-session GSMTC events, debounces noisy event bursts, refreshes snapshots off direct UI callbacks, and verifies session eligibility before commands.
- `MediaStateStore` stores normalized `MediaSessionSnapshot` values and applies app filtering before deterministic session selection.
- `TimelineController` extrapolates display position while playback is playing and reconciles optimistic seek operations against real timeline snapshots.
- `ArtworkCache` decodes thumbnails after metadata changes, keeps prior artwork during short null/decode-failure windows, and uses capped LRU plus TTL eviction.
- `EnhancedGSMTCMediaSessionManager` adapts the enhanced service back into the existing `MediaSessionManager`/`MediaSession` UI contract so ModernFlyouts' existing flyout visuals remain intact.

## Session Priority

Default policy:

1. Prefer the pinned app only when it currently has a playing eligible session.
2. Otherwise choose the most recently meaningfully changed playing session.
3. If nothing is playing, choose the most recently changed paused session.
4. Ignore stopped/closed sessions and sessions removed by app filtering.

`AlwaysPreferPinnedIfEligible` is opt-in. It allows a paused pinned app to override another playing app, so it is disabled by default.

## Settings Migration

Added settings:

- `EnhancedMediaBackendMode`: default `Auto`.
- `EnhancedMediaBackendLastPreflightSucceeded`: default `false`, updated after preflight without changing the selected backend mode.
- `ShowMediaPlayerInfo`: default enabled.
- `ShowMediaSeekbar`: default enabled.
- `ShowMediaShuffle`: default enabled.
- `ShowMediaRepeat`: default enabled.
- `PinnedMediaAppUserModelId`: default empty.
- `PinnedAppPriorityMode`: default `PreferPinnedOnlyWhenPlaying`.
- `MediaAppFilteringMode`: default `Disabled`.
- `MediaAppFilterList`: default empty.

No existing settings were removed. Missing or invalid new values fall back through `DefaultValuesStore`.

`Auto` enables the enhanced backend for the run only after preflight passes. If preflight fails, the existing legacy `NowPlayingMediaSessionManager` remains active and the audio settings page shows the diagnostic message.

## Manual Test Checklist

- YouTube Music: title, artist, source app, cover, duration, smooth elapsed time, and seekbar are shown.
- Seekbar drag sends `TryChangePlaybackPositionAsync`; failed or non-converging seeks revert within about two seconds.
- Spotify plus paused browser metadata selects Spotify while Spotify is playing.
- Pinned paused app does not override a different playing app by default.
- `AlwaysPreferPinnedIfEligible` allows the pinned paused app to override when explicitly enabled.
- Blocklist removes the blocked playing app before priority selection.
- Closing the displayed session selects the next eligible session or clears media content.
- Thumbnail null or decode failure keeps previous artwork briefly instead of flashing blank.
- Volume flyout, brightness flyout, airplane mode, and lock-key flyouts still open.

## Future Work

Taskbar media widget and taskbar visualizer are intentionally separate future work. They require new shell/taskbar surfaces, multi-monitor taskbar behavior, audio visualization capture/rendering, and conflict handling with taskbar modification tools.
