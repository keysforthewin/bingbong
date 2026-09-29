using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace bingbong
{
    public class AudioDevice
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;

        public override string ToString() => Name;
    }

    public enum DeviceMatchKind
    {
        /// <summary>The saved name matched a device exactly.</summary>
        Exact,
        /// <summary>A device was found whose name starts with a trimmed version of the saved name.</summary>
        Prefix,
        /// <summary>No device matched (or nothing is saved); the Windows default device is used.</summary>
        Default
    }

    public sealed class DeviceMatch
    {
        public AudioDevice? Device { get; }
        public DeviceMatchKind Kind { get; }
        /// <summary>The (possibly shortened) name that produced the match. Empty for Default.</summary>
        public string MatchedPrefix { get; }

        public DeviceMatch(AudioDevice? device, DeviceMatchKind kind, string matchedPrefix)
        {
            Device = device;
            Kind = kind;
            MatchedPrefix = matchedPrefix;
        }
    }

    public class AudioPlayer : IDisposable
    {
        /// <summary>Shortest prefix we are willing to accept as a match.</summary>
        private const int MinPrefixLength = 3;

        private string _selectedDeviceName = string.Empty;
        private float _volume = 1.0f;
        private bool _disposed;

        // Device-change notifications
        private readonly MMDeviceEnumerator _notifyEnumerator;
        private readonly DeviceNotificationClient _notificationClient;
        private readonly Timer _deviceChangeDebounce;
        private const int DeviceChangeDebounceMs = 500;

        /// <summary>
        /// Raised (on a background thread) roughly half a second after Windows reports
        /// any audio endpoint being added, removed, enabled, disabled, or made default.
        /// </summary>
        public event Action? DevicesChanged;

        public AudioPlayer()
        {
            _deviceChangeDebounce = new Timer(_ => DevicesChanged?.Invoke(), null, Timeout.Infinite, Timeout.Infinite);
            _notificationClient = new DeviceNotificationClient(OnDeviceChangeSignal);
            _notifyEnumerator = new MMDeviceEnumerator();
            try
            {
                _notifyEnumerator.RegisterEndpointNotificationCallback(_notificationClient);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Could not register for device notifications: {ex.Message}");
            }
        }

        public float Volume
        {
            get => _volume;
            set => _volume = Math.Clamp(value, 0f, 1f);
        }

        /// <summary>
        /// Friendly name of the preferred output device. Empty means "Windows default".
        /// The device is resolved by name every time a sound plays, so re-plugged or
        /// renumbered devices are picked up without a restart.
        /// </summary>
        public string SelectedDeviceName
        {
            get => _selectedDeviceName;
            set => _selectedDeviceName = value ?? string.Empty;
        }

        private void OnDeviceChangeSignal()
        {
            // Windows fires a burst of events per plug/unplug; coalesce them.
            try { _deviceChangeDebounce.Change(DeviceChangeDebounceMs, Timeout.Infinite); }
            catch (ObjectDisposedException) { }
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
        /// Finds the device for a saved friendly name.
        /// 1. Exact (case-insensitive) name match.
        /// 2. Otherwise drop characters from the end of the saved name one at a time and
        ///    take the first device whose name starts with what is left. This copes with
        ///    Windows appending things like " [2]" or ".2" after a re-plug.
        /// 3. Otherwise report Default; the caller should keep the saved name so the
        ///    device is picked up again when it comes back.
        /// </summary>
        public static DeviceMatch FindDeviceByName(string? savedName, IReadOnlyList<AudioDevice> devices)
        {
            var wanted = (savedName ?? string.Empty).Trim();
            if (wanted.Length == 0)
                return new DeviceMatch(null, DeviceMatchKind.Default, string.Empty);

            var exact = devices.FirstOrDefault(d =>
                string.Equals(d.Name.Trim(), wanted, StringComparison.OrdinalIgnoreCase));
            if (exact != null)
                return new DeviceMatch(exact, DeviceMatchKind.Exact, wanted);

            var candidate = wanted;
            while (candidate.Length >= MinPrefixLength)
            {
                var prefix = candidate;
                var hit = devices.FirstOrDefault(d =>
                    d.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
                if (hit != null)
                    return new DeviceMatch(hit, DeviceMatchKind.Prefix, prefix);

                candidate = candidate.Substring(0, candidate.Length - 1).TrimEnd();
            }

            return new DeviceMatch(null, DeviceMatchKind.Default, string.Empty);
        }

        /// <summary>
        /// Resolves the currently selected name against the live device list.
        /// </summary>
        public DeviceMatch ResolveSelectedDevice() => FindDeviceByName(_selectedDeviceName, GetOutputDevices());

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
            try
            {
                using var enumerator = new MMDeviceEnumerator();

                // Resolve by name at play time so a device that was re-plugged (and got a
                // new endpoint ID) is still found.
                MMDevice? targetDevice = null;
                var match = FindDeviceByName(_selectedDeviceName, GetOutputDevices());
                if (match.Device != null)
                {
                    try { targetDevice = enumerator.GetDevice(match.Device.Id); }
                    catch { targetDevice = null; }
                }
                targetDevice ??= enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

                using var audioFile = CreateAudioReader(filePath);
                if (audioFile == null) return;

                using var outputDevice = new WasapiOut(targetDevice, AudioClientShareMode.Shared, true, 200);
                outputDevice.Volume = volume;
                outputDevice.Init(audioFile);

                var playbackDone = new ManualResetEventSlim(false);
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

            try { _notifyEnumerator.UnregisterEndpointNotificationCallback(_notificationClient); } catch { }
            _notifyEnumerator.Dispose();
            _deviceChangeDebounce.Dispose();
        }

        /// <summary>
        /// Receives WASAPI endpoint notifications and forwards them as a single signal.
        /// </summary>
        private sealed class DeviceNotificationClient : IMMNotificationClient
        {
            private readonly Action _signal;

            public DeviceNotificationClient(Action signal) => _signal = signal;

            public void OnDeviceStateChanged(string deviceId, DeviceState newState) => _signal();
            public void OnDeviceAdded(string pwstrDeviceId) => _signal();
            public void OnDeviceRemoved(string deviceId) => _signal();
            public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
            {
                if (flow == DataFlow.Render) _signal();
            }
            public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
        }
    }
}
