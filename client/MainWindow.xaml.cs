using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace bingbong
{
    public partial class MainWindow : Window
    {
        private const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        private const string AppName = "bingbong";

        private AppConfig _config;
        private readonly AudioPlayer _audioPlayer;
        private readonly WebSocketClient _wsClient;
        private readonly ObservableCollection<SoundMapping> _mappings = new();
        private bool _isConnected;
        private System.Windows.Forms.NotifyIcon? _notifyIcon;

        public MainWindow()
        {
            InitializeComponent();

            _config = ConfigManager.Load();
            _audioPlayer = new AudioPlayer();
            _wsClient = new WebSocketClient();

            // Wire up events
            _wsClient.MessageReceived += OnMessageReceived;
            _wsClient.StateChanged += OnStateChanged;
            _wsClient.Log += OnLog;
            _wsClient.Error += OnError;

            // Load config into UI
            LoadConfigToUI();
            RefreshAudioDevices();
            SetupSystemTray();

            if (!string.IsNullOrEmpty(_config.WebSocketUrl))
            {
                _wsClient.Pin = _config.Pin;
                _ = _wsClient.ConnectAsync(_config.WebSocketUrl);
            }
        }

        private void SetupSystemTray()
        {
            _notifyIcon = new System.Windows.Forms.NotifyIcon
            {
                Text = "bingbong",
                Icon = CreateTrayIcon(),
                Visible = true
            };

            var contextMenu = new System.Windows.Forms.ContextMenuStrip();
            contextMenu.Items.Add("Show", null, (s, e) => ShowWindow());
            contextMenu.Items.Add("Exit", null, (s, e) => ExitApplication());
            _notifyIcon.ContextMenuStrip = contextMenu;

            _notifyIcon.DoubleClick += (s, e) => ShowWindow();
        }

        private static System.Drawing.Icon CreateTrayIcon()
        {
            var bitmap = new System.Drawing.Bitmap(32, 32);
            using var g = System.Drawing.Graphics.FromImage(bitmap);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(System.Drawing.Color.Transparent);

            // Purple circle background
            using (var brush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(124, 58, 237)))
                g.FillEllipse(brush, 1, 1, 30, 30);

            // White speaker body
            g.FillRectangle(System.Drawing.Brushes.White, 7, 11, 6, 10);

            // White speaker cone
            var cone = new System.Drawing.PointF[]
            {
                new(13, 8), new(20, 4), new(20, 28), new(13, 24)
            };
            g.FillPolygon(System.Drawing.Brushes.White, cone);

            // Sound wave arc
            using var pen = new System.Drawing.Pen(System.Drawing.Color.White, 2f);
            g.DrawArc(pen, 21, 9, 7, 14, -60, 120);

            return System.Drawing.Icon.FromHandle(bitmap.GetHicon());
        }

        private void ShowWindow()
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
        }

        private void ExitApplication()
        {
            _notifyIcon?.Dispose();
            _wsClient.Dispose();
            _audioPlayer.Dispose();
            Application.Current.Shutdown();
        }

        private void LoadConfigToUI()
        {
            txtWebSocketUrl.Text = _config.WebSocketUrl;
            txtPin.Text = _config.Pin;
            chkLaunchAtStartup.IsChecked = GetLaunchAtStartup();
            sldVolume.Value = _config.Volume * 100;
            txtVolumeLabel.Text = $"{(int)(sldVolume.Value)}%";

            _audioPlayer.Volume = _config.Volume;
            _audioPlayer.SelectedDeviceId = _config.SelectedAudioDeviceId;

            _wsClient.ReconnectDelayMs = _config.ReconnectDelayMs;
            _wsClient.MaxReconnectDelayMs = _config.MaxReconnectDelayMs;

            _mappings.Clear();
            foreach (var m in ConfigManager.LoadSoundMappings(_config))
            {
                _mappings.Add(m);
            }
            lstMappings.ItemsSource = _mappings;
        }

        private void SaveConfig()
        {
            _config.WebSocketUrl = txtWebSocketUrl.Text.Trim();
            _config.Pin = txtPin.Text.Trim();
            _config.Volume = (float)(sldVolume.Value / 100.0);
            _config.Volumes = _mappings.ToDictionary(m => m.Name, m => m.Volume);

            if (cboAudioDevice.SelectedItem is AudioDevice device)
            {
                _config.SelectedAudioDeviceId = device.Id;
            }

            ConfigManager.Save(_config);
        }

        #region Audio Devices

        private void RefreshAudioDevices()
        {
            var devices = AudioPlayer.GetOutputDevices();
            cboAudioDevice.ItemsSource = devices;

            // Try to reselect the saved device
            var saved = devices.FirstOrDefault(d => d.Id == _config.SelectedAudioDeviceId);
            if (saved != null)
            {
                cboAudioDevice.SelectedItem = saved;
            }
            else if (devices.Count > 0)
            {
                cboAudioDevice.SelectedIndex = 0;
            }
        }

        private void BtnRefreshDevices_Click(object sender, RoutedEventArgs e)
        {
            RefreshAudioDevices();
            AppendLog("Audio devices refreshed.");
        }

        private void CboAudioDevice_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (cboAudioDevice.SelectedItem is AudioDevice device)
            {
                _audioPlayer.SelectedDeviceId = device.Id;
                SaveConfig();
            }
        }

        private void BtnTestAudio_Click(object sender, RoutedEventArgs e)
        {
            // Play the first mapped sound or a system beep
            var first = _mappings.FirstOrDefault();
            if (first != null && System.IO.File.Exists(first.FilePath))
            {
                _audioPlayer.Play(first.FilePath);
                AppendLog($"Testing audio with: {first.Name}");
            }
            else
            {
                AppendLog("No sound mappings to test. Add a sound first.");
            }
        }

        private void SldVolume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (txtVolumeLabel == null) return;
            txtVolumeLabel.Text = $"{(int)sldVolume.Value}%";
            _audioPlayer.Volume = (float)(sldVolume.Value / 100.0);
            SaveConfig();
        }

        #endregion

        #region Sound Mappings

        private void BtnBrowseFile_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "Select Audio File",
                Filter = "Audio Files|*.wav;*.mp3;*.aiff;*.aif;*.wma;*.ogg|All Files|*.*"
            };

            if (dlg.ShowDialog() == true)
            {
                txtNewFilePath.Text = dlg.FileName;
            }
        }

        private void BtnAddMapping_Click(object sender, RoutedEventArgs e)
        {
            string name = txtNewName.Text.Trim();
            string path = txtNewFilePath.Text.Trim();

            if (string.IsNullOrEmpty(name))
            {
                MessageBox.Show("Please enter a trigger name.", "Missing Name",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
            {
                MessageBox.Show("Please select a valid audio file.", "Missing File",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (_mappings.Any(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                MessageBox.Show($"A mapping named '{name}' already exists.", "Duplicate",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                string destPath = ConfigManager.AddSound(name, path);
                float volume = (float)(sldNewVolume.Value / 100.0);
                _mappings.Add(new SoundMapping { Name = name, FilePath = destPath, Volume = volume });
                txtNewName.Text = "";
                txtNewFilePath.Text = "";
                sldNewVolume.Value = 100;

                SaveConfig();
                AppendLog($"Added sound mapping: '{name}'");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to copy audio file: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnRemoveMapping_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn && btn.Tag is string name)
            {
                var mapping = _mappings.FirstOrDefault(m => m.Name == name);
                if (mapping != null)
                {
                    ConfigManager.RemoveSound(name);
                    _mappings.Remove(mapping);
                    SaveConfig();
                    AppendLog($"Removed sound mapping: '{name}'");
                }
            }
        }

        private void BtnPlayMapping_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn && btn.Tag is string name)
            {
                PlaySoundByName(name);
            }
        }

        private void SldNewVolume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (txtNewVolumeLabel != null)
                txtNewVolumeLabel.Text = $"{(int)sldNewVolume.Value}%";
        }

        private void SldMappingVolume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (sender is System.Windows.Controls.Slider slider && slider.Tag is string name)
            {
                var mapping = _mappings.FirstOrDefault(m => m.Name == name);
                if (mapping != null)
                {
                    mapping.Volume = (float)(slider.Value / 100.0);
                    SaveConfig();
                }
            }
        }

        #endregion

        #region WebSocket Connection

        private async void BtnConnect_Click(object sender, RoutedEventArgs e)
        {
            if (_isConnected)
            {
                await _wsClient.DisconnectAsync();
            }
            else
            {
                string url = txtWebSocketUrl.Text.Trim();
                if (string.IsNullOrEmpty(url))
                {
                    MessageBox.Show("Please enter a WebSocket URL.", "Missing URL",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                SaveConfig();
                _wsClient.Pin = _config.Pin;
                await _wsClient.ConnectAsync(url);
            }
        }

        private void OnMessageReceived(string message)
        {
            Dispatcher.BeginInvoke(() =>
            {
                ProcessMessage(message);
            });
        }

        private void ProcessMessage(string message)
        {
            // Try to match the message to a sound
            // Supported formats:
            //   "Play <name>"
            //   "play <name>"
            //   Just "<name>" (direct match)
            //   JSON: { "action": "play", "sound": "<name>" }

            string? soundName = null;

            // Try "Play <name>" format
            if (message.StartsWith("Play ", StringComparison.OrdinalIgnoreCase))
            {
                soundName = message.Substring(5).Trim();
            }
            // Try JSON format
            else if (message.TrimStart().StartsWith("{"))
            {
                try
                {
                    var obj = Newtonsoft.Json.Linq.JObject.Parse(message);
                    soundName = obj["sound"]?.ToString()
                             ?? obj["name"]?.ToString()
                             ?? obj["play"]?.ToString();
                }
                catch { /* Not valid JSON, fall through */ }
            }

            // Fall back to direct name match
            if (string.IsNullOrEmpty(soundName))
            {
                soundName = message.Trim();
            }

            PlaySoundByName(soundName);
        }

        private void PlaySoundByName(string name)
        {
            var mapping = _mappings.FirstOrDefault(m =>
                m.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

            if (mapping != null)
            {
                AppendLog($"▶ Playing: {mapping.Name}");
                _audioPlayer.Play(mapping.FilePath, mapping.Volume);
            }
            else
            {
                AppendLog($"⚠ No mapping found for: '{name}'");
            }
        }

        private void OnStateChanged(ConnectionState state)
        {
            Dispatcher.BeginInvoke(() =>
            {
                switch (state)
                {
                    case ConnectionState.Connected:
                        StatusDot.Fill = (SolidColorBrush)FindResource("SuccessBrush");
                        StatusText.Text = "Connected";
                        btnConnect.Content = "Disconnect";
                        _isConnected = true;
                        txtWebSocketUrl.IsEnabled = false;
                        txtPin.IsEnabled = false;
                        break;

                    case ConnectionState.Connecting:
                        StatusDot.Fill = (SolidColorBrush)FindResource("WarningBrush");
                        StatusText.Text = "Connecting...";
                        btnConnect.Content = "Cancel";
                        _isConnected = true;
                        txtWebSocketUrl.IsEnabled = false;
                        txtPin.IsEnabled = false;
                        break;

                    case ConnectionState.Reconnecting:
                        StatusDot.Fill = (SolidColorBrush)FindResource("WarningBrush");
                        StatusText.Text = "Reconnecting...";
                        btnConnect.Content = "Cancel";
                        _isConnected = true;
                        break;

                    case ConnectionState.Disconnected:
                        StatusDot.Fill = (SolidColorBrush)FindResource("ErrorBrush");
                        StatusText.Text = "Disconnected";
                        btnConnect.Content = "Connect";
                        _isConnected = false;
                        txtWebSocketUrl.IsEnabled = true;
                        txtPin.IsEnabled = true;
                        break;
                }
            });
        }

        private void OnError(string error)
        {
            Dispatcher.BeginInvoke(() =>
            {
                // Show a Windows toast notification if minimized to tray
                if (!IsVisible && _notifyIcon != null)
                {
                    _notifyIcon.ShowBalloonTip(
                        3000,
                        "bingbong - Connection Error",
                        error,
                        System.Windows.Forms.ToolTipIcon.Warning
                    );
                }
            });
        }

        private void OnLog(string message)
        {
            Dispatcher.BeginInvoke(() => AppendLog(message));
        }

        #endregion

        #region Log

        private void AppendLog(string message)
        {
            string timestamped = message.StartsWith("[")
                ? message
                : $"[{DateTime.Now:HH:mm:ss}] {message}";

            txtLog.AppendText(timestamped + Environment.NewLine);
            txtLog.ScrollToEnd();

            // Keep log from growing unbounded
            if (txtLog.Text.Length > 50000)
            {
                txtLog.Text = txtLog.Text.Substring(txtLog.Text.Length - 30000);
            }
        }

        private void BtnClearLog_Click(object sender, RoutedEventArgs e)
        {
            txtLog.Clear();
        }

        #endregion

        #region Launch at Startup

        private static bool GetLaunchAtStartup()
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(AppName) != null;
        }

        private static void SetLaunchAtStartup(bool enable)
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key == null) return;
            if (enable)
                key.SetValue(AppName, $"\"{Environment.ProcessPath}\"");
            else
                key.DeleteValue(AppName, throwOnMissingValue: false);
        }

        private void ChkLaunchAtStartup_Changed(object sender, RoutedEventArgs e)
        {
            SetLaunchAtStartup(chkLaunchAtStartup.IsChecked ?? false);
        }

        #endregion

        #region Window Events

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            e.Cancel = true;
            Hide();
        }

        private void Window_StateChanged(object sender, EventArgs e)
        {
            if (WindowState == WindowState.Minimized)
            {
                Hide();
            }
        }

        #endregion
    }
}
