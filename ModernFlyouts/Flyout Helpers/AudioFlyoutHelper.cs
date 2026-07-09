using ModernFlyouts.Controls;
using ModernFlyouts.Core.Media.Control;
using ModernFlyouts.Core.Utilities;
using ModernFlyouts.Helpers;
using ModernFlyouts.Utilities;
using NAudio.CoreAudioApi;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ModernFlyouts
{
    public class AudioFlyoutHelper : FlyoutHelperBase
    {
        private AudioDeviceNotificationClient client;
        private MMDeviceEnumerator enumerator;
        private MMDevice device;
        private VolumeControl volumeControl;
        private SessionsPanel sessionsPanel;
        private TextBlock noDeviceMessageBlock;
        private List<MediaSessionManager> mediaSessionManagers = new();
        private CancellationTokenSource mediaBackendCancellationTokenSource;
        private bool mediaBackendRefreshInProgress;
        private bool mediaBackendStarted;
        private bool isFlyoutVisible;
        private bool isInitializing;
        private bool isVolumeFlyout = true;

        #region Properties

        public CompositeCollection AllMediaSessions { get; } = new();

        private bool showGSMTCInVolumeFlyout = true;

        public bool ShowGSMTCInVolumeFlyout
        {
            get => showGSMTCInVolumeFlyout;
            set
            {
                if (SetProperty(ref showGSMTCInVolumeFlyout, value))
                {
                    OnShowGSMTCInVolumeFlyoutChanged();
                }
            }
        }

        private bool showVolumeControlInGSMTCFlyout = true;

        public bool ShowVolumeControlInGSMTCFlyout
        {
            get => showVolumeControlInGSMTCFlyout;
            set
            {
                if (SetProperty(ref showVolumeControlInGSMTCFlyout, value))
                {
                    OnShowVolumeControlInGSMTCFlyoutChanged();
                }
            }
        }

        private EnhancedMediaBackendMode enhancedMediaBackendMode = DefaultValuesStore.EnhancedMediaBackendMode;

        public EnhancedMediaBackendMode EnhancedMediaBackendMode
        {
            get => enhancedMediaBackendMode;
            set
            {
                if (SetProperty(ref enhancedMediaBackendMode, value))
                {
                    AppDataHelper.EnhancedMediaBackendMode = value;
                    if (!isInitializing && mediaBackendStarted)
                    {
                        _ = RefreshMediaSessionBackendAsync();
                    }
                }
            }
        }

        private bool showPlayerInfo = DefaultValuesStore.ShowMediaPlayerInfo;

        public bool ShowPlayerInfo
        {
            get => showPlayerInfo;
            set
            {
                if (SetProperty(ref showPlayerInfo, value))
                {
                    AppDataHelper.ShowMediaPlayerInfo = value;
                }
            }
        }

        private bool showSeekbar = DefaultValuesStore.ShowMediaSeekbar;

        public bool ShowSeekbar
        {
            get => showSeekbar;
            set
            {
                if (SetProperty(ref showSeekbar, value))
                {
                    AppDataHelper.ShowMediaSeekbar = value;
                    RefreshMediaTimelineActivity();
                }
            }
        }

        private bool showShuffle = DefaultValuesStore.ShowMediaShuffle;

        public bool ShowShuffle
        {
            get => showShuffle;
            set
            {
                if (SetProperty(ref showShuffle, value))
                {
                    AppDataHelper.ShowMediaShuffle = value;
                }
            }
        }

        private bool showRepeat = DefaultValuesStore.ShowMediaRepeat;

        public bool ShowRepeat
        {
            get => showRepeat;
            set
            {
                if (SetProperty(ref showRepeat, value))
                {
                    AppDataHelper.ShowMediaRepeat = value;
                }
            }
        }

        private string pinnedMediaAppUserModelId = string.Empty;

        public string PinnedMediaAppUserModelId
        {
            get => pinnedMediaAppUserModelId;
            set
            {
                if (SetProperty(ref pinnedMediaAppUserModelId, value ?? string.Empty))
                {
                    AppDataHelper.PinnedMediaAppUserModelId = pinnedMediaAppUserModelId;
                    RefreshEnhancedSelectionOptions();
                }
            }
        }

        private PinnedAppPriorityMode pinnedAppPriorityMode = DefaultValuesStore.PinnedAppPriorityMode;

        public PinnedAppPriorityMode PinnedAppPriorityMode
        {
            get => pinnedAppPriorityMode;
            set
            {
                if (SetProperty(ref pinnedAppPriorityMode, value))
                {
                    AppDataHelper.PinnedAppPriorityMode = value;
                    RefreshEnhancedSelectionOptions();
                }
            }
        }

        private MediaAppFilteringMode mediaAppFilteringMode = DefaultValuesStore.MediaAppFilteringMode;

        public MediaAppFilteringMode MediaAppFilteringMode
        {
            get => mediaAppFilteringMode;
            set
            {
                if (SetProperty(ref mediaAppFilteringMode, value))
                {
                    AppDataHelper.MediaAppFilteringMode = value;
                    RefreshEnhancedSelectionOptions();
                }
            }
        }

        private string mediaAppFilterList = string.Empty;

        public string MediaAppFilterList
        {
            get => mediaAppFilterList;
            set
            {
                if (SetProperty(ref mediaAppFilterList, value ?? string.Empty))
                {
                    AppDataHelper.MediaAppFilterList = mediaAppFilterList;
                    RefreshEnhancedSelectionOptions();
                }
            }
        }

        private string enhancedBackendDiagnosticsMessage = string.Empty;

        public string EnhancedBackendDiagnosticsMessage
        {
            get => enhancedBackendDiagnosticsMessage;
            private set
            {
                if (SetProperty(ref enhancedBackendDiagnosticsMessage, value ?? string.Empty))
                {
                    OnPropertyChanged(nameof(EnhancedBackendDiagnosticsVisible));
                }
            }
        }

        public bool EnhancedBackendDiagnosticsVisible => !string.IsNullOrWhiteSpace(EnhancedBackendDiagnosticsMessage);

        #endregion

        public AudioFlyoutHelper()
        {
            Initialize();
        }

        public void Initialize()
        {
            AlwaysHandleDefaultFlyout = true;

            isInitializing = true;
            ShowGSMTCInVolumeFlyout = AppDataHelper.ShowGSMTCInVolumeFlyout;
            ShowVolumeControlInGSMTCFlyout = AppDataHelper.ShowVolumeControlInGSMTCFlyout;
            EnhancedMediaBackendMode = AppDataHelper.EnhancedMediaBackendMode;
            ShowPlayerInfo = AppDataHelper.ShowMediaPlayerInfo;
            ShowSeekbar = AppDataHelper.ShowMediaSeekbar;
            ShowShuffle = AppDataHelper.ShowMediaShuffle;
            ShowRepeat = AppDataHelper.ShowMediaRepeat;
            PinnedMediaAppUserModelId = AppDataHelper.PinnedMediaAppUserModelId;
            PinnedAppPriorityMode = AppDataHelper.PinnedAppPriorityMode;
            MediaAppFilteringMode = AppDataHelper.MediaAppFilteringMode;
            MediaAppFilterList = AppDataHelper.MediaAppFilterList;
            isInitializing = false;

            #region Volume control sub-module initialization

            volumeControl = new VolumeControl();
            volumeControl.VolumeButton.Click += VolumeButton_Click;
            volumeControl.VolumeSlider.ValueChanged += VolumeSlider_ValueChanged;
            volumeControl.VolumeSlider.PreviewMouseWheel += VolumeSlider_PreviewMouseWheel;

            noDeviceMessageBlock = new TextBlock()
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 18.0,
                Margin = new Thickness(20),
                Text = Properties.Strings.AudioFlyoutHelper_NoDevices
            };
            noDeviceMessageBlock.SetResourceReference(FrameworkElement.StyleProperty, "BaseTextBlockStyle");

            #endregion

            #region Media Session sub-module initialization

            FlyoutHandler.Initialized += (_, _) =>
            {
                sessionsPanel = new();
                SecondaryContent = sessionsPanel;
            };

            #endregion

            PrimaryContent = volumeControl;
            client = new AudioDeviceNotificationClient();

            enumerator = new MMDeviceEnumerator();
            enumerator.RegisterEndpointNotificationCallback(client);

            if (enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
            {
                UpdateDevice(enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia));
            }

            OnEnabled();
        }

        public override bool CanHandleNativeOnScreenFlyout(FlyoutTriggerData triggerData)
        {
            bool isMediaKey = triggerData.TriggerType == FlyoutTriggerType.Media;
            isVolumeFlyout = triggerData.TriggerType == FlyoutTriggerType.Volume;
            if (!isMediaKey && !isVolumeFlyout)
                return false;

            ValidatePrimaryContentVisible();
            ValidateSecondaryContentVisible();

            if (isMediaKey && !mediaBackendStarted)
            {
                return true;
            }

            if ((isVolumeFlyout && PrimaryContentVisible) || (isMediaKey && SecondaryContentVisible))
            {
                return true;
            }

            return base.CanHandleNativeOnScreenFlyout(triggerData);
        }

        private void OnShowGSMTCInVolumeFlyoutChanged()
        {
            ValidateSecondaryContentVisible();

            AppDataHelper.ShowGSMTCInVolumeFlyout = showGSMTCInVolumeFlyout;
        }

        private void ValidatePrimaryContentVisible()
        {
            PrimaryContentVisible = device != null && (isVolumeFlyout || showVolumeControlInGSMTCFlyout);
        }

        private void ValidateSecondaryContentVisible()
        {
            SecondaryContentVisible = AnyMediaSessionsAvailable() && (!isVolumeFlyout || showGSMTCInVolumeFlyout);
            RefreshMediaTimelineActivity();
        }

        private void OnShowVolumeControlInGSMTCFlyoutChanged()
        {
            ValidatePrimaryContentVisible();

            AppDataHelper.ShowVolumeControlInGSMTCFlyout = showVolumeControlInGSMTCFlyout;
        }

        #region Volume

        private void Client_DefaultDeviceChanged(object sender, string e)
        {
            MMDevice mmdevice = string.IsNullOrEmpty(e) ? null : enumerator.GetDevice(e);
            UpdateDevice(mmdevice);
        }

        private void UpdateDevice(MMDevice mmdevice)
        {
            if (device != null)
            {
                device.AudioEndpointVolume.OnVolumeNotification -= AudioEndpointVolume_OnVolumeNotification;
            }

            device = mmdevice;
            if (device != null)
            {
                try
                {
                    UpdateVolume(device.AudioEndpointVolume.MasterVolumeLevelScalar * 100);
                    device.AudioEndpointVolume.OnVolumeNotification += AudioEndpointVolume_OnVolumeNotification;
                }
                catch { }

                Application.Current.Dispatcher.Invoke(() => PrimaryContent = volumeControl);
            }
            else { Application.Current.Dispatcher.Invoke(() => PrimaryContent = noDeviceMessageBlock); }
        }

        private void AudioEndpointVolume_OnVolumeNotification(AudioVolumeNotificationData data)
        {
            UpdateVolume(data.MasterVolume * 100);
        }

        private bool _isInCodeValueChange; //Prevents a LOOP between changing volume.

        private void UpdateVolume(double volume)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                UpdateVolumeGlyph(volume);
                volumeControl.textVal.Text = Math.Round(volume).ToString("00");
                _isInCodeValueChange = true;
                volumeControl.VolumeSlider.Value = volume;
                _isInCodeValueChange = false;
            });
        }

        private void UpdateVolumeGlyph(double volume)
        {
            if (device != null && !device.AudioEndpointVolume.Mute)
            {
                volumeControl.VolumeShadowGlyph.Visibility = Visibility.Visible;
                if (volume >= 66)
                    volumeControl.VolumeGlyph.Glyph = CommonGlyphs.Volume3;
                else if (volume < 1)
                    volumeControl.VolumeGlyph.Glyph = CommonGlyphs.Volume0;
                else if (volume < 33)
                    volumeControl.VolumeGlyph.Glyph = CommonGlyphs.Volume1;
                else if (volume < 66)
                    volumeControl.VolumeGlyph.Glyph = CommonGlyphs.Volume2;

                volumeControl.textVal.ClearValue(TextBlock.ForegroundProperty);
                volumeControl.VolumeSlider.IsEnabled = true;
            }
            else
            {
                volumeControl.textVal.SetResourceReference(TextBlock.ForegroundProperty, "SystemControlForegroundBaseMediumBrush");
                volumeControl.VolumeSlider.IsEnabled = false;
                volumeControl.VolumeShadowGlyph.Visibility = Visibility.Collapsed;
                volumeControl.VolumeGlyph.Glyph = CommonGlyphs.Mute;
            }
        }

        private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_isInCodeValueChange)
            {
                var value = e.NewValue;
                var oldValue = e.OldValue;

                if (value == oldValue)
                {
                    return;
                }

                if (oldValue != value && device != null)
                {
                    try
                    {
                        device.AudioEndpointVolume.MasterVolumeLevelScalar = (float)(value / 100);
                    }
                    catch { } //99.9% is "A device attached to the system is not functioning" (0x8007001F), ignore this
                    
                    e.Handled = true;
                }
            }
        }

        private void VolumeButton_Click(object sender, RoutedEventArgs e)
        {
            if (device != null)
            {
                device.AudioEndpointVolume.Mute = !device.AudioEndpointVolume.Mute;
            }
        }

        private void VolumeSlider_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            var slider = sender as Slider;
            var change = e.Delta / 120.0;

            var volume = Math.Min(Math.Max(slider.Value + change, 0.0), 100.0);

            if (device != null)
            {
                try
                {
                    device.AudioEndpointVolume.MasterVolumeLevelScalar = (float)(volume / 100.0);
                }
                catch { }

                e.Handled = true;
            }
        }

        #endregion

        #region Media Control

        private void SetupMediaSessionManagers()
        {
            var npMediaSessionManager = new NowPlayingMediaSessionManager();
            SwitchMediaSessionManager(npMediaSessionManager);
        }

        private void MediaSessionManager_MediaSessionsChanged(object sender, EventArgs e)
        {
            ValidateSecondaryContentVisible();
        }

        private bool AnyMediaSessionsAvailable() => mediaSessionManagers.Any(x => x.ContainsAnySession());

        private void EnsureMediaSessionBackendStarted()
        {
            if (mediaBackendStarted || mediaBackendRefreshInProgress)
            {
                return;
            }

            mediaBackendStarted = true;
            _ = RefreshMediaSessionBackendAsync();
        }

        private bool IsMediaTimelineDisplayed()
        {
            return isFlyoutVisible && ShowSeekbar && SecondaryContentVisible;
        }

        private void RefreshMediaTimelineActivity()
        {
            foreach (var manager in mediaSessionManagers.OfType<EnhancedGSMTCMediaSessionManager>())
            {
                manager.RefreshTimelineActivity();
            }
        }

        private async Task RefreshMediaSessionBackendAsync()
        {
            mediaBackendCancellationTokenSource?.Cancel();
            mediaBackendCancellationTokenSource?.Dispose();
            mediaBackendCancellationTokenSource = new CancellationTokenSource();
            var refreshCancellationTokenSource = mediaBackendCancellationTokenSource;
            var cancellationToken = refreshCancellationTokenSource.Token;
            mediaBackendRefreshInProgress = true;

            try
            {
                if (EnhancedMediaBackendMode == ModernFlyouts.Core.Media.Control.EnhancedMediaBackendMode.Disabled)
                {
                    EnhancedBackendDiagnosticsMessage = string.Empty;
                    SwitchMediaSessionManager(new NowPlayingMediaSessionManager());
                    return;
                }

                var preflight = await MediaControlPreflight.RunAsync(cancellationToken);

                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                if (preflight.IsAvailable)
                {
                    AppDataHelper.EnhancedMediaBackendLastPreflightSucceeded = true;
                    EnhancedBackendDiagnosticsMessage = string.Empty;
                    SwitchMediaSessionManager(new EnhancedGSMTCMediaSessionManager(CreateSelectionOptions, IsMediaTimelineDisplayed));
                    return;
                }

                AppDataHelper.EnhancedMediaBackendLastPreflightSucceeded = false;
                EnhancedBackendDiagnosticsMessage = preflight.DiagnosticMessage;
                SwitchMediaSessionManager(new NowPlayingMediaSessionManager());
            }
            finally
            {
                if (ReferenceEquals(mediaBackendCancellationTokenSource, refreshCancellationTokenSource))
                {
                    mediaBackendRefreshInProgress = false;
                }
            }
        }

        private void StopMediaSessionBackend()
        {
            mediaBackendCancellationTokenSource?.Cancel();
            mediaBackendCancellationTokenSource?.Dispose();
            mediaBackendCancellationTokenSource = null;
            mediaBackendRefreshInProgress = false;
            mediaBackendStarted = false;

            foreach (var mediaSessionManager in mediaSessionManagers)
            {
                mediaSessionManager.MediaSessionsChanged -= MediaSessionManager_MediaSessionsChanged;
                mediaSessionManager.OnDisabled();
            }

            mediaSessionManagers.Clear();
            AllMediaSessions.Clear();
            ValidateSecondaryContentVisible();
        }

        private MediaSessionSelectionOptions CreateSelectionOptions()
        {
            return MediaSessionSelectionOptions.FromDelimitedList(
                PinnedMediaAppUserModelId,
                PinnedAppPriorityMode,
                MediaAppFilteringMode,
                MediaAppFilterList);
        }

        private void RefreshEnhancedSelectionOptions()
        {
            if (isInitializing)
            {
                return;
            }

            foreach (var manager in mediaSessionManagers.OfType<EnhancedGSMTCMediaSessionManager>())
            {
                manager.RefreshSelectionOptions();
            }
        }

        private void SwitchMediaSessionManager(MediaSessionManager mediaSessionManager)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                foreach (var manager in mediaSessionManagers)
                {
                    manager.MediaSessionsChanged -= MediaSessionManager_MediaSessionsChanged;

                    if (IsEnabled)
                    {
                        manager.OnDisabled();
                    }
                }

                mediaSessionManagers.Clear();
                AllMediaSessions.Clear();

                mediaSessionManagers.Add(mediaSessionManager);
                AllMediaSessions.Add(new CollectionContainer { Collection = mediaSessionManager.MediaSessions });
                mediaSessionManager.MediaSessionsChanged += MediaSessionManager_MediaSessionsChanged;

                if (IsEnabled)
                {
                    mediaSessionManager.OnEnabled();
                }

                ValidateSecondaryContentVisible();
            });
        }

        #endregion

        #region Media Session Fallback Thumbnails

        public static ImageSource GetDefaultAudioThumbnail() => new BitmapImage(PackUriHelper.GetAbsoluteUri("Assets/Images/DefaultAudioThumbnail.png"));

        public static ImageSource GetDefaultImageThumbnail() => new BitmapImage(PackUriHelper.GetAbsoluteUri("Assets/Images/DefaultImageThumbnail.png"));

        public static ImageSource GetDefaultVideoThumbnail() => new BitmapImage(PackUriHelper.GetAbsoluteUri("Assets/Images/DefaultVideoThumbnail.png"));

        #endregion

        protected override void OnEnabled()
        {
            base.OnEnabled();

            AppDataHelper.AudioModuleEnabled = IsEnabled;

            if (!IsEnabled)
            {
                return;
            }

            client.DefaultDeviceChanged += Client_DefaultDeviceChanged;

            if (device != null)
            {
                device.AudioEndpointVolume.OnVolumeNotification += AudioEndpointVolume_OnVolumeNotification;
                PrimaryContent = volumeControl;
            }
            else { PrimaryContent = noDeviceMessageBlock; }

            ValidatePrimaryContentVisible();

            if (mediaBackendStarted)
            {
                foreach (var mediaSessionManager in mediaSessionManagers)
                {
                    mediaSessionManager.OnEnabled();
                }
            }
        }

        protected override void OnDisabled()
        {
            base.OnDisabled();

            isFlyoutVisible = false;
            StopMediaSessionBackend();

            client.DefaultDeviceChanged -= Client_DefaultDeviceChanged;

            if (device != null)
            {
                device.AudioEndpointVolume.OnVolumeNotification -= AudioEndpointVolume_OnVolumeNotification;
            }

            PrimaryContent = null;
            PrimaryContentVisible = false;

            AppDataHelper.AudioModuleEnabled = IsEnabled;
        }

        public override void OnFlyoutShown()
        {
            isFlyoutVisible = true;
            EnsureMediaSessionBackendStarted();
            RefreshMediaTimelineActivity();
        }

        public override void OnFlyoutHidden()
        {
            isFlyoutVisible = false;
            StopMediaSessionBackend();
        }
    }
}
