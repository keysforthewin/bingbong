using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace SoundRelay
{
    public class SoundMapping
    {
        public string Name { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty;
    }

    public class AppConfig
    {
        public string WebSocketUrl { get; set; } = "ws://localhost:8080";
        public int ReconnectDelayMs { get; set; } = 3000;
        public int MaxReconnectDelayMs { get; set; } = 30000;
        public string SelectedAudioDeviceId { get; set; } = string.Empty;
        public float Volume { get; set; } = 1.0f;
        public List<SoundMapping> SoundMappings { get; set; } = new();
        public bool MinimizeToTray { get; set; } = true;
        public bool StartMinimized { get; set; } = false;
    }

    public static class ConfigManager
    {
        private static readonly string ConfigDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SoundRelay"
        );

        private static readonly string ConfigPath = Path.Combine(ConfigDir, "config.json");

        public static AppConfig Load()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    string json = File.ReadAllText(ConfigPath);
                    return JsonConvert.DeserializeObject<AppConfig>(json) ?? new AppConfig();
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
                string json = JsonConvert.SerializeObject(config, Formatting.Indented);
                File.WriteAllText(ConfigPath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error saving config: {ex.Message}");
            }
        }
    }
}
