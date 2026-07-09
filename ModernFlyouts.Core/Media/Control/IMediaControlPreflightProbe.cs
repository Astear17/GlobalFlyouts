using System.Collections.Generic;
using System.Threading.Tasks;

namespace ModernFlyouts.Core.Media.Control
{
    public interface IMediaControlPreflightProbe
    {
        bool IsMediaControlTypeVisible();

        Task<object> RequestManagerAsync();

        IReadOnlyList<object> GetSessions(object manager);

        object GetPlaybackInfo(object session);

        object GetTimelineProperties(object session);

        Task<object> TryGetMediaPropertiesAsync(object session);
    }
}
