using System;
using System.Windows;

namespace ModernFlyouts.Core.Media.Control
{
    public sealed class EnhancedGSMTCMediaSessionManager : MediaSessionManager
    {
        private readonly Func<MediaSessionSelectionOptions> getSelectionOptions;
        private readonly Func<bool> isTimelineDisplayActive;
        private MediaSessionService service;
        private EnhancedGSMTCMediaSession displaySession;
        private bool enabled;

        public EnhancedGSMTCMediaSessionManager(
            Func<MediaSessionSelectionOptions> getSelectionOptions,
            Func<bool> isTimelineDisplayActive)
        {
            this.getSelectionOptions = getSelectionOptions;
            this.isTimelineDisplayActive = isTimelineDisplayActive ?? (() => false);
        }

        public void RefreshSelectionOptions()
        {
            service?.UpdateSelectionOptions();
        }

        public void RefreshTimelineActivity()
        {
            displaySession?.RefreshTimelineActivity();
        }

        public override async void OnEnabled()
        {
            if (enabled)
            {
                return;
            }

            enabled = true;
            service = new MediaSessionService(getSelectionOptions);
            service.SelectionChanged += Service_SelectionChanged;
            service.SessionsChanged += Service_SessionsChanged;
            await service.InitializeAsync();
        }

        public override void OnDisabled()
        {
            enabled = false;

            if (service != null)
            {
                service.SelectionChanged -= Service_SelectionChanged;
                service.SessionsChanged -= Service_SessionsChanged;
                service.Dispose();
                service = null;
            }

            Application.Current?.Dispatcher.Invoke(ClearDisplaySession);
        }

        public override bool ContainsAnySession()
        {
            return CurrentMediaSession != null && MediaSessions.Count > 0;
        }

        private void Service_SelectionChanged(object sender, MediaSessionSelection selection)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
            {
                ApplySelection(selection);
            }
            else
            {
                dispatcher.BeginInvoke(new Action(() => ApplySelection(selection)));
            }
        }

        private void Service_SessionsChanged(object sender, EventArgs e)
        {
            RaiseMediaSessionsChanged();
        }

        private void ApplySelection(MediaSessionSelection selection)
        {
            if (!enabled)
            {
                return;
            }

            if (selection?.Snapshot == null)
            {
                ClearDisplaySession();
                RaiseMediaSessionsChanged();
                return;
            }

            if (displaySession == null)
            {
                displaySession = new EnhancedGSMTCMediaSession(service, selection.Snapshot, isTimelineDisplayActive);
                MediaSessions.Add(displaySession);
                CurrentMediaSession = displaySession;
                displaySession.IsCurrent = true;
                RaiseMediaSessionsChanged();
                return;
            }

            displaySession.ApplySnapshot(selection.Snapshot);
            CurrentMediaSession = displaySession;
            displaySession.IsCurrent = true;
        }

        private void ClearDisplaySession()
        {
            if (displaySession != null)
            {
                displaySession.Disconnect();
                displaySession = null;
            }

            foreach (var session in MediaSessions)
            {
                session.Disconnect();
            }

            MediaSessions.Clear();
            CurrentMediaSession = null;
        }
    }
}
