using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace bingbong
{
    public partial class MainWindow : Window
    {
        private const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        private const string AppName = "bingbong";
        private const string DefaultDeviceLabel = "Windows default device";
        private static readonly string[] AudioExtensions = { ".wav", ".mp3", ".aiff", ".aif", ".wma", ".ogg" };

        private AppConfig _config;
        private readonly AudioPlayer _audioPlayer;
        private readonly WebSocketClient _wsClient;
        private readonly UpdateService _updater = new();
        private readonly ObservableCollection<SoundMapping> _mappings = new();
        private bool _isConnected;
        private System.Windows.Forms.NotifyIcon? _notifyIcon;

        /// <summary>True while code (not the user) is changing the device combo.</summary>
        private bool _suppressDeviceSave;
        /// <summary>True once LoadConfigToUI has run; TextChanged handlers are ignored before that.</summary>
        private bool _uiReady;
        /// <summary>Full path of the file waiting in the "add sound" panel.</summary>
        private string? _pendingFilePath;

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
            _audioPlayer.DevicesChanged += OnAudioDevicesChanged;
            _mappings.CollectionChanged += (s, e) => OnMappingsChanged();

            // Load config into UI
            LoadConfigToUI();
            RefreshAudioDevices();
            SetupSystemTray();
            _uiReady = true;
            RefreshIntegrateText();

            // Auto-update from GitHub Releases
            txtVersion.Text = $"v{UpdateService.CurrentVersionText}";
            _updater.IsEnabled = () => _config.AutoUpdate;
            _updater.Log += m => Dispatcher.BeginInvoke(() => AppendLog(m));
            _updater.Restarting += _ => Dispatcher.BeginInvoke(ExitApplication);
            _updater.Start();

            if (!string.IsNullOrEmpty(_config.WebSocketUrl))
            {
                _wsClient.Pin = _config.Pin;
                _ = _wsClient.ConnectAsync(_config.WebSocketUrl);
            }
        }

        #region Tray

        private void SetupSystemTray()
        {
            _notifyIcon = new System.Windows.Forms.NotifyIcon
            {
                Text = "bingbong",
                Icon = LoadTrayIcon(),
                Visible = true
            };

            var contextMenu = new System.Windows.Forms.ContextMenuStrip();
            contextMenu.Items.Add("Show", null, (s, e) => ShowWindow());
            contextMenu.Items.Add("Exit", null, (s, e) => ExitApplication());
            _notifyIcon.ContextMenuStrip = contextMenu;

            _notifyIcon.DoubleClick += (s, e) => ShowWindow();
        }

        /// <summary>Loads the tray icon from the embedded bingbong.ico (same art as the window and exe).</summary>
        private static System.Drawing.Icon LoadTrayIcon()
        {
            try
            {
                var res = Application.GetResourceStream(new Uri("pack://application:,,,/bingbong.ico"));
                if (res != null)
                {
                    using var stream = res.Stream;
                    return new System.Drawing.Icon(stream, new System.Drawing.Size(32, 32));
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Could not load tray icon: {ex.Message}");
            }
            return System.Drawing.SystemIcons.Application;
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
            _updater.Dispose();
            _wsClient.Dispose();
            _audioPlayer.Dispose();
            Application.Current.Shutdown();
        }

        #endregion

        #region Config <-> UI

        private void LoadConfigToUI()
        {
            txtWebSocketUrl.Text = _config.WebSocketUrl;
            txtPin.Text = _config.Pin;
            txtTriggerBase.Text = _config.TriggerBaseUrl;
            chkLaunchAtStartup.IsChecked = GetLaunchAtStartup();
            chkAutoUpdate.IsChecked = _config.AutoUpdate;
            sldVolume.Value = _config.Volume * 100;
            txtVolumeLabel.Text = $"{(int)(sldVolume.Value)}%";

            _audioPlayer.Volume = _config.Volume;
            _audioPlayer.SelectedDeviceName = _config.SelectedAudioDeviceName;

            _wsClient.ReconnectDelayMs = _config.ReconnectDelayMs;
            _wsClient.MaxReconnectDelayMs = _config.MaxReconnectDelayMs;

            _mappings.Clear();
            foreach (var m in ConfigManager.LoadSoundMappings(_config))
            {
                _mappings.Add(m);
            }
            lstMappings.ItemsSource = _mappings;
            OnMappingsChanged();

            txtFooterHost.Text = HostLabel(_config.WebSocketUrl);
        }

        private void SaveConfig()
        {
            _config.WebSocketUrl = txtWebSocketUrl.Text.Trim();
            _config.Pin = txtPin.Text.Trim();
            _config.TriggerBaseUrl = txtTriggerBase.Text.Trim();
            _config.AutoUpdate = chkAutoUpdate.IsChecked ?? true;
            _config.Volume = (float)(sldVolume.Value / 100.0);
            _config.Volumes = _mappings.ToDictionary(m => m.Name, m => m.Volume);
            // _config.SelectedAudioDeviceName is updated directly when the user picks a device.

            ConfigManager.Save(_config);
            RefreshIntegrateText();
        }

        private void OnMappingsChanged()
        {
            txtNoSounds.Visibility = _mappings.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            RefreshExampleSounds();
        }

        #endregion

        #region Navigation

        private void Nav_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.CommandParameter is string page)
                ShowPage(page);
        }

        private void ShowPage(string page)
        {
            pageSounds.Visibility = page == "Sounds" ? Visibility.Visible : Visibility.Collapsed;
            pageOutput.Visibility = page == "Output" ? Visibility.Visible : Visibility.Collapsed;
            pageConnection.Visibility = page == "Connection" ? Visibility.Visible : Visibility.Collapsed;
            pageIntegrate.Visibility = page == "Integrate" ? Visibility.Visible : Visibility.Collapsed;
            pageActivity.Visibility = page == "Activity" ? Visibility.Visible : Visibility.Collapsed;

            navSounds.Tag = page == "Sounds" ? "Active" : null;
            navOutput.Tag = page == "Output" ? "Active" : null;
            navConnection.Tag = page == "Connection" ? "Active" : null;
            navIntegrate.Tag = page == "Integrate" ? "Active" : null;
            navActivity.Tag = page == "Activity" ? "Active" : null;

            if (page == "Integrate") RefreshIntegrateText();
            if (page == "Activity") txtLog.ScrollToEnd();
        }

        #endregion

        #region Audio Devices

        /// <summary>
        /// Re-enumerates devices and re-selects the saved one by name. Never saves:
        /// programmatic selection must not overwrite the user's choice.
        /// </summary>
        private void RefreshAudioDevices()
        {
            var devices = AudioPlayer.GetOutputDevices();
            var items = new System.Collections.Generic.List<AudioDevice>
            {
                new AudioDevice { Id = string.Empty, Name = DefaultDeviceLabel }
            };
            items.AddRange(devices);

            var match = AudioPlayer.FindDeviceByName(_config.SelectedAudioDeviceName, devices);

            _suppressDeviceSave = true;
            try
            {
                cboAudioDevice.ItemsSource = items;
                cboAudioDevice.SelectedItem = match.Device ?? items[0];
            }
            finally
            {
                _suppressDeviceSave = false;
            }

            ShowDeviceMatchStatus(match);
        }

        private void ShowDeviceMatchStatus(DeviceMatch match)
        {
            string saved = _config.SelectedAudioDeviceName;
            switch (match.Kind)
            {
                case DeviceMatchKind.Exact:
                    txtDeviceStatus.Text = "Matched by name.";
                    txtDeviceStatus.Foreground = (Brush)FindResource("SuccessTextBrush");
                    txtFooterDevice.Text = match.Device!.Name;
                    break;

                case DeviceMatchKind.Prefix:
                    txtDeviceStatus.Text =
                        $"Saved '{saved}' was not found exactly. Matched '{match.MatchedPrefix}' → '{match.Device!.Name}'.";
                    txtDeviceStatus.Foreground = (Brush)FindResource("SuccessTextBrush");
                    txtFooterDevice.Text = match.Device.Name;
                    break;

                default:
                    if (string.IsNullOrEmpty(saved))
                    {
                        txtDeviceStatus.Text = "Using the Windows default device.";
                        txtDeviceStatus.Foreground = (Brush)FindResource("TextDimBrush");
                    }
                    else
                    {
                        txtDeviceStatus.Text =
                            $"Saved device '{saved}' not found. Using the Windows default until it returns.";
                        txtDeviceStatus.Foreground = (Brush)FindResource("WarningBrush");
                    }
                    txtFooterDevice.Text = DefaultDeviceLabel;
                    break;
            }
        }

        private void BtnRefreshDevices_Click(object sender, RoutedEventArgs e)
        {
            RefreshAudioDevices();
            AppendLog("Audio devices refreshed.");
        }

        private void OnAudioDevicesChanged()
        {
            Dispatcher.BeginInvoke(() =>
            {
                RefreshAudioDevices();
                AppendLog($"Audio devices changed. Output is now: {txtFooterDevice.Text}");
            });
        }

        private void CboAudioDevice_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressDeviceSave) return;
            if (cboAudioDevice.SelectedItem is not AudioDevice device) return;

            // The synthetic default entry has an empty Id; store an empty name for it.
            string name = string.IsNullOrEmpty(device.Id) ? string.Empty : device.Name;
            _config.SelectedAudioDeviceName = name;
            _audioPlayer.SelectedDeviceName = name;
            SaveConfig();

            var kind = string.IsNullOrEmpty(name) ? DeviceMatchKind.Default : DeviceMatchKind.Exact;
            ShowDeviceMatchStatus(new DeviceMatch(string.IsNullOrEmpty(name) ? null : device, kind, name));
            AppendLog($"Output device set to: {device.Name}");
        }

        private void BtnTestAudio_Click(object sender, RoutedEventArgs e)
        {
            var first = _mappings.FirstOrDefault();
            if (first != null && System.IO.File.Exists(first.FilePath))
            {
                _audioPlayer.Play(first.FilePath, first.Volume);
                AppendLog($"Testing audio with: {first.Name}");
            }
            else
            {
                AppendLog("No sounds to test. Add a sound first.");
            }
        }

        private void SldVolume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (txtVolumeLabel == null) return;
            txtVolumeLabel.Text = $"{(int)sldVolume.Value}%";
            _audioPlayer.Volume = (float)(sldVolume.Value / 100.0);
            if (_uiReady) SaveConfig();
        }

        #endregion

        #region Sounds

        private static bool IsAudioFile(string path) =>
            AudioExtensions.Contains(System.IO.Path.GetExtension(path).ToLowerInvariant());

        private void BtnAddSound_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "Choose an audio file",
                Filter = "Audio Files|*.wav;*.mp3;*.aiff;*.aif;*.wma;*.ogg|All Files|*.*"
            };

            if (dlg.ShowDialog() == true)
                BeginAddSound(dlg.FileName);
        }

        /// <summary>Shows the inline confirm panel with the name pre-filled from the file.</summary>
        private void BeginAddSound(string path)
        {
            _pendingFilePath = path;
            txtNewFileName.Text = $"Adding {System.IO.Path.GetFileName(path)}";
            txtNewName.Text = System.IO.Path.GetFileNameWithoutExtension(path);
            sldNewVolume.Value = 100;
            pnlNewSound.Visibility = Visibility.Visible;
            ShowPage("Sounds");
            txtNewName.Focus();
            txtNewName.SelectAll();
        }

        private void TxtNewName_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) BtnConfirmAdd_Click(sender, e);
            else if (e.Key == Key.Escape) BtnCancelAdd_Click(sender, e);
        }

        private void BtnConfirmAdd_Click(object sender, RoutedEventArgs e)
        {
            string name = txtNewName.Text.Trim();
            string? path = _pendingFilePath;

            if (string.IsNullOrEmpty(name))
            {
                MessageBox.Show("Please enter a name for the sound.", "Missing name",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
            {
                MessageBox.Show("The chosen audio file no longer exists.", "Missing file",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (_mappings.Any(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                MessageBox.Show($"A sound named '{name}' already exists.", "Duplicate",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                string destPath = ConfigManager.AddSound(name, path);
                float volume = (float)(sldNewVolume.Value / 100.0);
                _mappings.Add(new SoundMapping { Name = name, FilePath = destPath, Volume = volume });

                BtnCancelAdd_Click(sender, e);
                SaveConfig();
                AppendLog($"Added sound: '{name}'");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to copy audio file: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnCancelAdd_Click(object sender, RoutedEventArgs e)
        {
            _pendingFilePath = null;
            txtNewName.Text = string.Empty;
            txtNewFileName.Text = string.Empty;
            sldNewVolume.Value = 100;
            pnlNewSound.Visibility = Visibility.Collapsed;
        }

        private void BtnRemoveMapping_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string name)
            {
                var mapping = _mappings.FirstOrDefault(m => m.Name == name);
                if (mapping != null)
                {
                    ConfigManager.RemoveSound(name);
                    _mappings.Remove(mapping);
                    SaveConfig();
                    AppendLog($"Removed sound: '{name}'");
                }
            }
        }

        private void BtnPlayMapping_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string name)
                PlaySoundByName(name);
        }

        private void SldNewVolume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (txtNewVolumeLabel != null)
                txtNewVolumeLabel.Text = $"{(int)sldNewVolume.Value}%";
        }

        private void SldMappingVolume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (sender is Slider slider && slider.Tag is string name)
            {
                var mapping = _mappings.FirstOrDefault(m => m.Name == name);
                if (mapping != null)
                {
                    mapping.Volume = (float)(slider.Value / 100.0);
                    SaveConfig();
                }
            }
        }

        private void Window_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = DragEventArgs_HasAudio(e) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            if (!DragEventArgs_HasAudio(e)) return;
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            var first = files.FirstOrDefault(IsAudioFile);
            if (first != null)
            {
                ShowWindow();
                BeginAddSound(first);
            }
            e.Handled = true;
        }

        private static bool DragEventArgs_HasAudio(DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return false;
            return e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Any(IsAudioFile);
        }

        #endregion

        #region WebSocket Connection

        private void ConnectionField_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_uiReady) RefreshIntegrateText();
        }

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
                txtFooterHost.Text = HostLabel(url);
                _wsClient.Pin = _config.Pin;
                await _wsClient.ConnectAsync(url);
            }
        }

        private void OnMessageReceived(string message)
        {
            Dispatcher.BeginInvoke(() => ProcessMessage(message));
        }

        private void ProcessMessage(string message)
        {
            // Supported formats:
            //   "Play <name>"
            //   Just "<name>" (direct match)
            //   JSON: { "sound": "<name>" } / { "name": ... } / { "play": ... }

            string? soundName = null;

            if (message.StartsWith("Play ", StringComparison.OrdinalIgnoreCase))
            {
                soundName = message.Substring(5).Trim();
            }
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

            if (string.IsNullOrEmpty(soundName))
                soundName = message.Trim();

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
                AppendLog($"⚠ No sound named: '{name}'");
            }
        }

        private void OnStateChanged(ConnectionState state)
        {
            Dispatcher.BeginInvoke(() =>
            {
                Brush dot;
                string text;

                switch (state)
                {
                    case ConnectionState.Connected:
                        dot = (Brush)FindResource("SuccessBrush");
                        text = "Connected";
                        btnConnect.Content = "Disconnect";
                        _isConnected = true;
                        break;

                    case ConnectionState.Connecting:
                        dot = (Brush)FindResource("WarningBrush");
                        text = "Connecting...";
                        btnConnect.Content = "Cancel";
                        _isConnected = true;
                        break;

                    case ConnectionState.Reconnecting:
                        dot = (Brush)FindResource("WarningBrush");
                        text = "Reconnecting...";
                        btnConnect.Content = "Cancel";
                        _isConnected = true;
                        break;

                    default:
                        dot = (Brush)FindResource("ErrorBrush");
                        text = "Disconnected";
                        btnConnect.Content = "Connect";
                        _isConnected = false;
                        break;
                }

                StatusDot.Fill = dot;
                StatusText.Text = text;
                ConnDot.Fill = dot;
                txtConnStatus.Text = text;
                txtWebSocketUrl.IsEnabled = !_isConnected;
                txtPin.IsEnabled = !_isConnected;
            });
        }

        private void OnError(string error)
        {
            Dispatcher.BeginInvoke(() =>
            {
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

        private static string HostLabel(string wsUrl)
        {
            if (Uri.TryCreate(wsUrl, UriKind.Absolute, out var uri))
                return uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
            return wsUrl;
        }

        #endregion

        #region Integrate

        private void RefreshExampleSounds()
        {
            if (cboExampleSound == null) return;

            var current = cboExampleSound.SelectedItem as string;
            var names = _mappings.Select(m => m.Name).ToList();
            cboExampleSound.ItemsSource = names;
            cboExampleSound.SelectedItem = current != null && names.Contains(current)
                ? current
                : names.FirstOrDefault();
        }

        private void CboExampleSound_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_uiReady) RefreshIntegrateText();
        }

        private void TxtTriggerBase_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_uiReady) RefreshIntegrateText();
        }

        /// <summary>
        /// Guess the HTTP trigger address from the WebSocket URL. The server's defaults
        /// are HTTP 3000 / WS 8080 (no .env) or HTTP 3260 / WS 3261 (the shipped .env).
        /// </summary>
        private static string DeriveHttpBase(string wsUrl)
        {
            if (!Uri.TryCreate(wsUrl, UriKind.Absolute, out var uri))
                return "http://localhost:3260";

            string scheme = uri.Scheme.Equals("wss", StringComparison.OrdinalIgnoreCase) ? "https" : "http";
            int port = uri.Port switch
            {
                3261 => 3260,
                8080 => 3000,
                _ => 3260
            };
            return $"{scheme}://{uri.Host}:{port}";
        }

        private void RefreshIntegrateText()
        {
            if (!_uiReady || txtSnipUrl == null) return;

            string wsUrl = txtWebSocketUrl.Text.Trim();
            string pin = txtPin.Text.Trim();
            string httpBase = string.IsNullOrWhiteSpace(txtTriggerBase.Text)
                ? DeriveHttpBase(wsUrl)
                : txtTriggerBase.Text.Trim().TrimEnd('/');
            string sound = cboExampleSound.SelectedItem as string
                           ?? _mappings.FirstOrDefault()?.Name
                           ?? "bing_bong";

            string wsWithPin = string.IsNullOrEmpty(pin)
                ? wsUrl
                : $"{wsUrl}{(wsUrl.Contains('?') ? '&' : '?')}pin={pin}";

            string triggerUrl = $"{httpBase}/bingbong/{sound}";

            txtSnipUrl.Text = triggerUrl;
            txtSnipCurl.Text = $"curl {triggerUrl}";
            txtSnipFetch.Text = $"await fetch('{triggerUrl}');";
            txtSnipListen.Text =
                "const WebSocket = require('ws');\n" +
                "\n" +
                "// Same address and PIN as this app uses\n" +
                $"const ws = new WebSocket('{wsWithPin}');\n" +
                "\n" +
                "ws.on('open', () => console.log('Connected to bingbong'));\n" +
                "\n" +
                "ws.on('message', (data) => {\n" +
                $"  const msg = String(data);        // e.g. \"Play {sound}\"\n" +
                "  if (msg.startsWith('Play ')) {\n" +
                $"    const sound = msg.slice(5);    // \"{sound}\"\n" +
                "    console.log('Play sound:', sound);\n" +
                "  }\n" +
                "});";

            txtPlain1.Text = "1. Your shop, script, or automation hits the trigger URL when something happens.";
            txtPlain2.Text = $"2. The bingbong server tells every connected computer \"Play {sound}\".";
            txtPlain3.Text = $"3. Each computer plays the file named {sound} on its chosen speakers.";

            if (string.IsNullOrWhiteSpace(txtTriggerBase.Text))
                txtTriggerBase.ToolTip = $"Derived from the WebSocket URL: {httpBase}. Type an address here if your server uses a different port.";
        }

        private void BtnCopy_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn) return;

            TextBox? source = (btn.Tag as string) switch
            {
                "url" => txtSnipUrl,
                "curl" => txtSnipCurl,
                "fetch" => txtSnipFetch,
                "listen" => txtSnipListen,
                _ => null
            };
            if (source == null) return;

            try
            {
                Clipboard.SetText(source.Text);
            }
            catch (Exception ex)
            {
                AppendLog($"Could not copy to clipboard: {ex.Message}");
                return;
            }

            var original = btn.Content;
            btn.Content = "Copied";
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            timer.Tick += (s, args) =>
            {
                btn.Content = original;
                timer.Stop();
            };
            timer.Start();
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

        #region Updates

        private void ChkAutoUpdate_Changed(object sender, RoutedEventArgs e)
        {
            if (!_uiReady) return;
            SaveConfig();
            AppendLog(_config.AutoUpdate ? "Automatic updates turned on." : "Automatic updates turned off.");
        }

        private async void BtnCheckUpdate_Click(object sender, RoutedEventArgs e)
        {
            btnCheckUpdate.IsEnabled = false;
            txtUpdateStatus.Text = "Checking GitHub…";
            try
            {
                var msg = await _updater.CheckAndInstallAsync(force: true) ?? "No update needed.";
                txtUpdateStatus.Text = msg;
                AppendLog(msg);
            }
            finally
            {
                btnCheckUpdate.IsEnabled = true;
            }
        }

        #endregion

        #region Launch at Startup

        private static bool GetLaunchAtStartup()
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(AppName) != null;
        }

        private static void SetLaunchAtStartup(bool enable)
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
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
