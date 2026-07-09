using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ModernFlyouts.Core.Media.Control
{
    public static class MediaControlPreflight
    {
        public static Task<MediaControlPreflightResult> RunAsync(CancellationToken cancellationToken = default)
        {
            return RunAsync(new GsmtcMediaControlPreflightProbe(), cancellationToken);
        }

        public static async Task<MediaControlPreflightResult> RunAsync(
            IMediaControlPreflightProbe probe,
            CancellationToken cancellationToken = default)
        {
            if (probe == null)
            {
                return MediaControlPreflightResult.Fail("Enhanced media backend unavailable: pre-flight probe is missing.");
            }

            try
            {
                if (!probe.IsMediaControlTypeVisible())
                {
                    return Fail("GSMTC media-control type is not visible.");
                }
            }
            catch (Exception ex)
            {
                return Fail($"GSMTC media-control type check failed: {ex.Message}");
            }

            object manager;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                manager = await probe.RequestManagerAsync();
            }
            catch (Exception ex)
            {
                return Fail($"GSMTC RequestAsync failed: {ex.Message}");
            }

            if (manager == null)
            {
                return Fail("GSMTC RequestAsync returned null manager.");
            }

            object[] sessions;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                sessions = probe.GetSessions(manager)?.ToArray() ?? Array.Empty<object>();
            }
            catch (Exception ex)
            {
                return Fail($"GSMTC GetSessions failed: {ex.Message}");
            }

            if (sessions.Length == 0)
            {
                var result = MediaControlPreflightResult.Pass(0, "Enhanced media backend available: API returned no active media sessions.");
                MediaDiagnostics.Info(result.DiagnosticMessage);
                return result;
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var firstSession = sessions[0];

                _ = probe.GetPlaybackInfo(firstSession);
                _ = probe.GetTimelineProperties(firstSession);
                _ = await probe.TryGetMediaPropertiesAsync(firstSession);
            }
            catch (Exception ex)
            {
                return Fail($"GSMTC session property read failed: {ex.Message}");
            }

            var pass = MediaControlPreflightResult.Pass(sessions.Length, $"Enhanced media backend available: {sessions.Length} GSMTC session(s) detected.");
            MediaDiagnostics.Info(pass.DiagnosticMessage);
            return pass;

            static MediaControlPreflightResult Fail(string message)
            {
                var result = MediaControlPreflightResult.Fail("Enhanced media backend unavailable: " + message);
                MediaDiagnostics.Warning(result.DiagnosticMessage);
                return result;
            }
        }
    }
}
