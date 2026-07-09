using ModernFlyouts.Core.Media.Control;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;

namespace ModernFlyouts.Core.Tests
{
    public class PreflightTests
    {
        [Fact]
        public async Task NoSessionsMeansApiAvailable()
        {
            var result = await MediaControlPreflight.RunAsync(new FakeProbe());

            Assert.True(result.IsAvailable);
            Assert.Equal(0, result.SessionCount);
        }

        [Fact]
        public async Task RequestFailureFallsBack()
        {
            var result = await MediaControlPreflight.RunAsync(new FakeProbe
            {
                RequestManager = () => throw new InvalidOperationException("boom")
            });

            Assert.False(result.IsAvailable);
            Assert.Contains("RequestAsync failed", result.DiagnosticMessage);
        }

        [Fact]
        public async Task NullManagerFallsBack()
        {
            var result = await MediaControlPreflight.RunAsync(new FakeProbe { Manager = null });

            Assert.False(result.IsAvailable);
            Assert.Contains("returned null manager", result.DiagnosticMessage);
        }

        [Fact]
        public async Task GetSessionsFailureFallsBack()
        {
            var result = await MediaControlPreflight.RunAsync(new FakeProbe
            {
                GetSessionsCallback = _ => throw new InvalidOperationException("sessions")
            });

            Assert.False(result.IsAvailable);
            Assert.Contains("GetSessions failed", result.DiagnosticMessage);
        }

        [Fact]
        public async Task PropertyReadFailureFallsBackWithoutCrash()
        {
            var result = await MediaControlPreflight.RunAsync(new FakeProbe
            {
                Sessions = new[] { new object() },
                GetPlaybackInfoCallback = _ => throw new InvalidOperationException("properties")
            });

            Assert.False(result.IsAvailable);
            Assert.Contains("property read failed", result.DiagnosticMessage);
        }

        [Fact]
        public async Task PropertyReadSuccessPasses()
        {
            var result = await MediaControlPreflight.RunAsync(new FakeProbe
            {
                Sessions = new[] { new object() }
            });

            Assert.True(result.IsAvailable);
            Assert.Equal(1, result.SessionCount);
        }

        private sealed class FakeProbe : IMediaControlPreflightProbe
        {
            public object Manager { get; init; } = new();

            public object[] Sessions { get; init; } = Array.Empty<object>();

            public Func<Task<object>> RequestManager { get; init; }

            public Func<object, IReadOnlyList<object>> GetSessionsCallback { get; init; }

            public Func<object, object> GetPlaybackInfoCallback { get; init; }

            public bool IsMediaControlTypeVisible() => true;

            public Task<object> RequestManagerAsync()
            {
                return RequestManager != null ? RequestManager() : Task.FromResult(Manager);
            }

            public IReadOnlyList<object> GetSessions(object manager)
            {
                return GetSessionsCallback != null ? GetSessionsCallback(manager) : Sessions;
            }

            public object GetPlaybackInfo(object session)
            {
                return GetPlaybackInfoCallback != null ? GetPlaybackInfoCallback(session) : new object();
            }

            public object GetTimelineProperties(object session)
            {
                return new object();
            }

            public Task<object> TryGetMediaPropertiesAsync(object session)
            {
                return Task.FromResult<object>(new object());
            }
        }
    }
}
