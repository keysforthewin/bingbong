using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace bingbong
{
    public sealed class UpdateInfo
    {
        public Version Version { get; init; } = new(0, 0, 0);
        public string Tag { get; init; } = string.Empty;
        public string DownloadUrl { get; init; } = string.Empty;
        public string ReleaseUrl { get; init; } = string.Empty;
        public long Size { get; init; }
    }

    /// <summary>
    /// Keeps bingbong up to date from GitHub Releases.
    ///
    /// Every few hours it asks GitHub for the latest release of keysforthewin/bingbong.
    /// If the tag is newer than this build, it downloads the release's bingbong.exe next
    /// to the running exe, renames the running exe to bingbong.exe.old (Windows allows
    /// renaming a running executable), moves the new one into place, starts it, and asks
    /// the window to exit. The next start deletes the .old file.
    /// </summary>
    public sealed class UpdateService : IDisposable
    {
        public const string Repo = "keysforthewin/bingbong";
        private const string AssetName = "bingbong.exe";
        private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

        private readonly HttpClient _http;
        private Timer? _timer;
        private int _busy;

        /// <summary>Human-readable progress messages for the activity log.</summary>
        public event Action<string>? Log;

        /// <summary>The new version has been started; the current process should exit now.</summary>
        public event Action<UpdateInfo>? Restarting;

        /// <summary>Read each time a background check runs, so the user's setting applies immediately.</summary>
        public Func<bool> IsEnabled { get; set; } = () => true;

        public static Version CurrentVersion =>
            Normalize(Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0));

        public static string CurrentVersionText =>
            $"{CurrentVersion.Major}.{CurrentVersion.Minor}.{CurrentVersion.Build}";

        public static string ExePath => Environment.ProcessPath ?? string.Empty;

        /// <summary>
        /// False when running under a debugger or straight out of a build folder, so a
        /// developer's bin\Debug exe is never swapped out from under them.
        /// </summary>
        public static bool CanSelfUpdate
        {
            get
            {
                if (Debugger.IsAttached) return false;
                var p = ExePath;
                if (!p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return false;
                if (p.Contains(@"\bin\Debug\", StringComparison.OrdinalIgnoreCase)) return false;
                if (p.Contains(@"\bin\Release\", StringComparison.OrdinalIgnoreCase) &&
                    !p.Contains(@"\publish\", StringComparison.OrdinalIgnoreCase)) return false;
                return true;
            }
        }

        public UpdateService()
        {
            _http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd($"bingbong/{CurrentVersionText}");
            _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        }

        /// <summary>Starts the periodic background check.</summary>
        public void Start()
        {
            CleanupOldExe();
            _timer = new Timer(_ =>
            {
                CleanupOldExe();
                var msg = CheckAndInstallAsync(force: false).GetAwaiter().GetResult();
                if (msg != null) Log?.Invoke(msg);
            }, null, FirstCheckDelay, CheckInterval);
        }

        private static void CleanupOldExe()
        {
            try
            {
                var old = ExePath + ".old";
                if (File.Exists(old)) File.Delete(old);
            }
            catch
            {
                // The previous version may still be shutting down; try again next time.
            }
        }

        private static Version Normalize(Version v) =>
            new(Math.Max(v.Major, 0), Math.Max(v.Minor, 0), Math.Max(v.Build, 0));

        /// <summary>Reads the latest release from GitHub. Null when there are no releases or no exe asset.</summary>
        public async Task<UpdateInfo?> FetchLatestAsync(CancellationToken ct = default)
        {
            using var resp = await _http.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest", ct);
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
            resp.EnsureSuccessStatusCode();

            var json = JObject.Parse(await resp.Content.ReadAsStringAsync(ct));
            var tag = json["tag_name"]?.ToString() ?? string.Empty;
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version)) return null;

            var asset = (json["assets"] as JArray)?.FirstOrDefault(a =>
                string.Equals(a["name"]?.ToString(), AssetName, StringComparison.OrdinalIgnoreCase));
            if (asset == null) return null;

            return new UpdateInfo
            {
                Version = Normalize(version),
                Tag = tag,
                DownloadUrl = asset["browser_download_url"]?.ToString() ?? string.Empty,
                ReleaseUrl = json["html_url"]?.ToString() ?? string.Empty,
                Size = asset["size"]?.Value<long>() ?? 0
            };
        }

        /// <summary>
        /// Checks for a newer release and, when allowed, downloads and installs it.
        /// Returns a status message to show the user, or null when a background check
        /// found nothing worth mentioning.
        /// </summary>
        public async Task<string?> CheckAndInstallAsync(bool force)
        {
            if (!force && !IsEnabled()) return null;
            if (Interlocked.Exchange(ref _busy, 1) == 1) return "An update check is already running.";

            try
            {
                var latest = await FetchLatestAsync();
                if (latest == null)
                    return force ? "No releases with a bingbong.exe were found on GitHub yet." : null;

                if (latest.Version <= CurrentVersion)
                    return force ? $"You're on the latest version ({CurrentVersionText})." : null;

                if (!CanSelfUpdate)
                    return $"Version {latest.Tag} is available at {latest.ReleaseUrl}. This copy is running from a build folder, so it won't replace itself.";

                Log?.Invoke($"Update {latest.Tag} available (you have {CurrentVersionText}). Downloading…");

                var newPath = ExePath + ".new";
                await DownloadAsync(latest.DownloadUrl, newPath);

                var downloaded = new FileInfo(newPath);
                if (latest.Size > 0 && downloaded.Length != latest.Size)
                {
                    downloaded.Delete();
                    return "The update download was incomplete. Will try again later.";
                }

                return Install(newPath, latest);
            }
            catch (Exception ex)
            {
                return $"Update check failed: {ex.Message}";
            }
            finally
            {
                Interlocked.Exchange(ref _busy, 0);
            }
        }

        private async Task DownloadAsync(string url, string destination)
        {
            using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();
            await using var input = await resp.Content.ReadAsStreamAsync();
            await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
            await input.CopyToAsync(output);
        }

        private string Install(string newPath, UpdateInfo info)
        {
            var exe = ExePath;
            var old = exe + ".old";

            try { if (File.Exists(old)) File.Delete(old); } catch { }

            try
            {
                File.Move(exe, old);
                File.Move(newPath, exe);
            }
            catch (Exception ex)
            {
                // Put things back so the app still starts next time.
                try { if (!File.Exists(exe) && File.Exists(old)) File.Move(old, exe); } catch { }
                try { if (File.Exists(newPath)) File.Delete(newPath); } catch { }
                return $"Could not install the update: {ex.Message}";
            }

            try
            {
                Process.Start(new ProcessStartInfo(exe)
                {
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(exe) ?? string.Empty
                });
            }
            catch (Exception ex)
            {
                return $"Installed {info.Tag} but could not restart automatically: {ex.Message}. Start bingbong again to use the new version.";
            }

            Log?.Invoke($"Installed {info.Tag}. Restarting…");
            Restarting?.Invoke(info);
            return $"Installed {info.Tag}. Restarting…";
        }

        public void Dispose()
        {
            _timer?.Dispose();
            _http.Dispose();
        }
    }
}
