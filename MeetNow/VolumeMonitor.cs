using NAudio.CoreAudioApi;
using Serilog;
using System;
using System.Windows;

namespace MeetNow
{
    internal static class VolumeMonitor
    {
        private static MMDevice? _device;
        private static float _initialVolume;
        private static bool _monitoring;
        private static bool _acted;

        public static void Start()
        {
            Stop();

            try
            {
                var enumerator = new MMDeviceEnumerator();
                _device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                _initialVolume = _device.AudioEndpointVolume.MasterVolumeLevelScalar;
                _acted = false;
                _monitoring = true;

                _device.AudioEndpointVolume.OnVolumeNotification += OnVolumeChanged;

                Log.Information("VolumeMonitor started. Initial volume: {Volume:P0}", _initialVolume);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to start VolumeMonitor");
            }
        }

        public static void Stop()
        {
            if (_device != null)
            {
                try
                {
                    _device.AudioEndpointVolume.OnVolumeNotification -= OnVolumeChanged;
                }
                catch { }
                _device = null;
            }
            _monitoring = false;
            _acted = false;
        }

        private static void OnVolumeChanged(AudioVolumeNotificationData data)
        {
            if (!_monitoring || _acted)
                return;

            float delta = data.MasterVolume - _initialVolume;

            // Ignore tiny changes (less than 2% threshold)
            if (Math.Abs(delta) < 0.02f)
                return;

            _acted = true;
            _monitoring = false;

            Log.Information("VolumeMonitor detected volume change: {Delta:+0.##;-0.##} (initial={Initial:P0}, new={New:P0})",
                delta, _initialVolume, data.MasterVolume);

            if (delta > 0)
            {
                // Volume UP → Join the first meeting
                Application.Current.Dispatcher.Invoke(() =>
                {
                    PopupEventsWindow.JoinFirstMeeting();
                });
            }
            else
            {
                // Volume DOWN → Cancel / close popups
                Application.Current.Dispatcher.Invoke(() =>
                {
                    PopupEventsWindow.CloseAllWindows();
                });
            }
        }
    }
}
