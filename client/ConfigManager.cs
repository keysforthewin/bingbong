using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;

namespace bingbong
{
    public class SoundMapping : INotifyPropertyChanged
    {
        private string _name = string.Empty;
        private string _filePath = string.Empty;
        private float _volume = 1.0f;

        [JsonIgnore]
        public string Name
        {
            get => _name;
            set { _name = value; OnPropertyChanged(); }
        }

        [JsonIgnore]
        public string FilePath
        {
            get => _filePath;
            set { _filePath = value; OnPropertyChanged(); }
        }

        [JsonIgnore]
        public float Volume
        {
            get => _volume;
            set { _volume = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public class AppConfig
    {
        public string WebSocketUrl { get; set; } = "ws://localhost:8080";
        public int ReconnectDelayMs { get; set; } = 3000;
        public int MaxReconnectDelayMs { get; set; } = 30000;
        public string SelectedAudioDeviceId { get; set; } = string.Empty;
        public float Volume { get; set; } = 1.0f;
        public Dictionary<string, float> Volumes { get; set; } = new();
        public bool MinimizeToTray { get; set; } = true;
        public bool StartMinimized { get; set; } = false;
        public string Pin { get; set; } = string.Empty;

        // Legacy field for migration only — not saved going forward
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public List<LegacySoundMapping>? SoundMappings { get; set; }
    }

    // Used only for deserializing old config.json during migration
    public class LegacySoundMapping
    {
        public string Name { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty;
        public float Volume { get; set; } = 1.0f;
    }

    public static class ConfigManager
    {
        private static readonly string ConfigDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "bingbong"
        );

        private static readonly string ConfigPath = Path.Combine(ConfigDir, "config.json");

        public static readonly string SoundsDir = Path.Combine(ConfigDir, "sounds");

        private static readonly string[] AudioExtensions = { ".wav", ".mp3", ".aiff", ".aif", ".wma", ".ogg" };

        public static AppConfig Load()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    string json = File.ReadAllText(ConfigPath);
                    var config = JsonConvert.DeserializeObject<AppConfig>(json) ?? new AppConfig();
                    MigrateIfNeeded(config);
                    return config;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error loading config: {ex.Message}");
            }
            return new AppConfig();
        }

        public static void Save(AppConfig config)
        {
            try
            {
                Directory.CreateDirectory(ConfigDir);
                // Clear legacy field so it's not written
                config.SoundMappings = null;
                string json = JsonConvert.SerializeObject(config, Formatting.Indented);
                File.WriteAllText(ConfigPath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error saving config: {ex.Message}");
            }
        }

        /// <summary>
        /// Scans the sounds folder for audio files and returns SoundMapping objects.
        /// Trigger name = filename without extension. Volume comes from config.
        /// </summary>
        public static List<SoundMapping> LoadSoundMappings(AppConfig config)
        {
            var mappings = new List<SoundMapping>();

            if (!Directory.Exists(SoundsDir))
                return mappings;

            foreach (var file in Directory.GetFiles(SoundsDir))
            {
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (!AudioExtensions.Contains(ext))
                    continue;

                var name = Path.GetFileNameWithoutExtension(file);
                config.Volumes.TryGetValue(name, out float volume);
                if (volume == 0f && !config.Volumes.ContainsKey(name))
                    volume = 1.0f;

                mappings.Add(new SoundMapping
                {
                    Name = name,
                    FilePath = file,
                    Volume = volume
                });
            }

            return mappings;
        }

        /// <summary>
        /// Copies an audio file into the sounds folder, named after the trigger.
        /// Returns the destination path.
        /// </summary>
        public static string AddSound(string triggerName, string sourceFilePath)
        {
            Directory.CreateDirectory(SoundsDir);
            var sanitized = SanitizeFileName(triggerName);
            var ext = Path.GetExtension(sourceFilePath);
            var destPath = Path.Combine(SoundsDir, sanitized + ext);
            File.Copy(sourceFilePath, destPath, overwrite: true);
            return destPath;
        }

        /// <summary>
        /// Deletes the sound file for a given trigger name (any extension).
        /// </summary>
        public static void RemoveSound(string triggerName)
        {
            if (!Directory.Exists(SoundsDir))
                return;

            var sanitized = SanitizeFileName(triggerName);
            foreach (var file in Directory.GetFiles(SoundsDir))
            {
                if (Path.GetFileNameWithoutExtension(file).Equals(sanitized, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(file);
                    return;
                }
            }
        }

        /// <summary>
        /// Strips characters that are invalid in Windows filenames.
        /// </summary>
        public static string SanitizeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var sanitized = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
            return string.IsNullOrWhiteSpace(sanitized) ? "_" : sanitized;
        }

        /// <summary>
        /// Migrates old SoundMappings (file paths in config) to file-based storage.
        /// </summary>
        private static void MigrateIfNeeded(AppConfig config)
        {
            if (config.SoundMappings == null || config.SoundMappings.Count == 0)
                return;

            // Only migrate if sounds folder is empty or doesn't exist
            if (Directory.Exists(SoundsDir) && Directory.GetFiles(SoundsDir).Length > 0)
            {
                config.SoundMappings = null;
                return;
            }

            Directory.CreateDirectory(SoundsDir);

            foreach (var mapping in config.SoundMappings)
            {
                if (string.IsNullOrEmpty(mapping.FilePath) || !File.Exists(mapping.FilePath))
                    continue;

                try
                {
                    var sanitized = SanitizeFileName(mapping.Name);
                    var ext = Path.GetExtension(mapping.FilePath);
                    var destPath = Path.Combine(SoundsDir, sanitized + ext);
                    File.Copy(mapping.FilePath, destPath, overwrite: true);

                    if (mapping.Volume != 1.0f)
                        config.Volumes[mapping.Name] = mapping.Volume;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Migration failed for '{mapping.Name}': {ex.Message}");
                }
            }

            config.SoundMappings = null;
            Save(config);
        }
    }
}
