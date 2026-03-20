
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MeetNow
{
    internal static class SfxHelper
    {
        static List<DirectSoundOut> playbacks = new();
        private const string PROXIMITY_SOUND = @"C:\Windows\Media\Windows Proximity Notification.wav";

        public static void PlayOnAllDevices()
        {
            var soundFile = File.Exists(PROXIMITY_SOUND)
                ? PROXIMITY_SOUND
                : @"C:\Windows\Media\Windows Notify Calendar.wav"; // fallback
            if (!File.Exists(soundFile))
            {
                Log.Warning("No notification sound found");
                return;
            }
            PlayFileOnAllDevices(soundFile);
        }

        public static void PlayFileOnAllDevices(string audioFilePath)
        {
            if (!File.Exists(audioFilePath)) return;

            var enumerator = new MMDeviceEnumerator();
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);

            foreach (var device in devices)
            {
                PlayOnDevice(device, audioFilePath);
            }
        }
        public static void StopAllDevices()
        {
            lock (playbacks)
            {
                foreach (var playback in playbacks)
                {
                    playback.Stop();
                }
            }
        }
        static void PlayOnDevice(MMDevice device, string audioFilePath)
        {
            var dsDevices = DirectSoundOut.Devices;
            var matchedDevice = dsDevices.FirstOrDefault(ds => ds.Description == device.FriendlyName || ds.ModuleName == device.FriendlyName);

            if (matchedDevice == null)
            {
                Log.Information($"Couldn't find a matching DirectSound device for {device.FriendlyName}");
                return;
            }
            var output = new DirectSoundOut(matchedDevice.Guid);
            var audioFile = new AudioFileReader(audioFilePath);
            output.Init(audioFile);
            output.Play();

            lock (playbacks)
            {
                playbacks.Add(output);
            }
            output.PlaybackStopped += (s, e) =>
            {
                output.Dispose();
                audioFile.Dispose();
                lock (playbacks)
                {
                    playbacks.Remove(output);
                }
            };
        }

    }
}
