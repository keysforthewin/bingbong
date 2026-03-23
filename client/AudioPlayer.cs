using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace bingbong
{
    public class AudioDevice
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;

        public override string ToString() => Name;
    }

    public class AudioPlayer : IDisposable
    {
        private string _selectedDeviceId = string.Empty;
        private float _volume = 1.0f;
        private bool _disposed;

        public float Volume
        {
            get => _volume;
            set => _volume = Math.Clamp(value, 0f, 1f);
        }

        public string SelectedDeviceId
        {
            get => _selectedDeviceId;
            set => _selectedDeviceId = value ?? string.Empty;
        }

        /// <summary>
        /// Enumerates all active audio output devices using WASAPI.
        /// </summary>
        public static List<AudioDevice> GetOutputDevices()
        {
            var devices = new List<AudioDevice>();

            try
            {
                using var enumerator = new MMDeviceEnumerator();
                var endpoints = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);

                foreach (var device in endpoints)
                {
                    devices.Add(new AudioDevice
                    {
                        Id = device.ID,
                        Name = device.FriendlyName
                    });
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error enumerating audio devices: {ex.Message}");
            }

            return devices;
        }

        /// <summary>
        /// Plays an audio file on the selected output device.
        /// Supports WAV, MP3, and other formats NAudio can handle.
        /// </summary>
        public void Play(string filePath, float? soundVolume = null)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            {
                System.Diagnostics.Debug.WriteLine($"Audio file not found: {filePath}");
                return;
            }

            float effectiveVolume = Math.Clamp((soundVolume ?? 1.0f) * _volume, 0f, 1f);

            // Fire and forget on a background thread so we don't block
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    PlayInternal(filePath, effectiveVolume);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error playing audio: {ex.Message}");
                }
            });
        }

        private void PlayInternal(string filePath, float volume)
        {
            MMDevice? targetDevice = null;

            try
            {
                using var enumerator = new MMDeviceEnumerator();

                if (!string.IsNullOrEmpty(_selectedDeviceId))
                {
                    try
                    {
                        targetDevice = enumerator.GetDevice(_selectedDeviceId);
                    }
                    catch
                    {
                        // Device not found, fall back to default
                        targetDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                    }
                }
                else
                {
                    targetDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                }

                using var audioFile = CreateAudioReader(filePath);
                if (audioFile == null) return;

                using var outputDevice = new WasapiOut(targetDevice, AudioClientShareMode.Shared, true, 200);
                outputDevice.Volume = volume;
                outputDevice.Init(audioFile);

                var playbackDone = new System.Threading.ManualResetEventSlim(false);
                outputDevice.PlaybackStopped += (s, e) => playbackDone.Set();

                outputDevice.Play();
                playbackDone.Wait(TimeSpan.FromMinutes(5)); // Safety timeout
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Playback error: {ex.Message}");
            }
        }

        private static WaveStream? CreateAudioReader(string filePath)
        {
            string ext = Path.GetExtension(filePath).ToLowerInvariant();

            try
            {
                return ext switch
                {
                    ".wav" => new WaveFileReader(filePath),
                    ".mp3" => new Mp3FileReader(filePath),
                    ".aiff" or ".aif" => new AiffFileReader(filePath),
                    _ => new AudioFileReader(filePath) // Let NAudio figure it out
                };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Cannot read audio file {filePath}: {ex.Message}");
                return null;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
        }
    }
}
