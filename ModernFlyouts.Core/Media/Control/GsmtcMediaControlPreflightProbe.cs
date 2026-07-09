using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.Media.Control;

namespace ModernFlyouts.Core.Media.Control
{
    public sealed class GsmtcMediaControlPreflightProbe : IMediaControlPreflightProbe
    {
        public bool IsMediaControlTypeVisible()
        {
            _ = typeof(GlobalSystemMediaTransportControlsSessionManager).FullName;
            return true;
        }

        public async Task<object> RequestManagerAsync()
        {
            return await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        }

        public IReadOnlyList<object> GetSessions(object manager)
        {
            return ((GlobalSystemMediaTransportControlsSessionManager)manager)
                .GetSessions()
                .Cast<object>()
                .ToArray();
        }

        public object GetPlaybackInfo(object session)
        {
            return ((GlobalSystemMediaTransportControlsSession)session).GetPlaybackInfo();
        }

        public object GetTimelineProperties(object session)
        {
            return ((GlobalSystemMediaTransportControlsSession)session).GetTimelineProperties();
        }

        public async Task<object> TryGetMediaPropertiesAsync(object session)
        {
            return await ((GlobalSystemMediaTransportControlsSession)session).TryGetMediaPropertiesAsync();
        }
    }
}
