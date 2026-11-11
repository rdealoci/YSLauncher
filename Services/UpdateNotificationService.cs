using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace YSLauncher.Services;

    public static class UpdateNotificationService
    {
        public static string ConfigDir => LauncherConfig.ConfigDir;
        public const string BlogUrl = "https://yanderedev.wordpress.com";
        public const string RssFeedUrl = "https://yanderedev.wordpress.com/feed/";

        public static async Task<(string postUrl, string postTitle, string? imagePath)> FetchLatestPostInfoAsync()
        {
            string fallbackUrl = BlogUrl;
            string? fallbackImage = null;
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
                client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) YSLauncher/1.0");
                string xml = await client.GetStringAsync(RssFeedUrl);
                var itemMatch = Regex.Match(xml, @"<item\b[^>]*>([\s\S]*?)</item>", RegexOptions.IgnoreCase);
                if (itemMatch.Success)
                {
                    string itemContent = itemMatch.Groups[1].Value;

                    // Extract post URL
                    string postUrl = fallbackUrl;
                    var linkMatch = Regex.Match(itemContent, @"<link\b[^>]*>(?:<!\[CDATA\[)?\s*(https?://[^\s<\]]+)\s*(?:\]\]>)?</link>", RegexOptions.IgnoreCase);
                    if (linkMatch.Success)
                    {
                        string foundUrl = linkMatch.Groups[1].Value.Trim();
                        if (Uri.TryCreate(foundUrl, UriKind.Absolute, out _))
                        {
                            postUrl = foundUrl;
                            LauncherConfig.LatestPostUrl = postUrl;
                        }
                    }

                    // Extract post title
                    string postTitle = "Yandere Simulator Update";
                    var titleMatch = Regex.Match(itemContent, @"<title\b[^>]*>(?:<!\[CDATA\[)?\s*(.*?)\s*(?:\]\]>)?</title>", RegexOptions.IgnoreCase);
                    if (titleMatch.Success && !string.IsNullOrWhiteSpace(titleMatch.Groups[1].Value))
                    {
                        postTitle = System.Net.WebUtility.HtmlDecode(titleMatch.Groups[1].Value.Trim());
                    }

                    // Extract media image URLs (excluding emojis and gravatars)
                    var imageUrls = new System.Collections.Generic.List<string>();
                    var mediaMatches = Regex.Matches(itemContent, @"<media:content[^>]+url=[\""\']([^\""\']+)[\""\']", RegexOptions.IgnoreCase);
                    foreach (Match m in mediaMatches) imageUrls.Add(m.Groups[1].Value);

                    var imgMatches = Regex.Matches(itemContent, @"<img[^>]+src=[\""\']([^\""\']+)[\""\']", RegexOptions.IgnoreCase);
                    foreach (Match m in imgMatches) imageUrls.Add(m.Groups[1].Value);

                    var validImages = imageUrls.FindAll(u =>
                        !u.Contains("wpcom-smileys") &&
                        !u.Contains("twemoji") &&
                        !u.Contains("gravatar.com") &&
                        (u.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                         u.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                         u.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                         u.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)));

                    string? downloadedImage = null;
                    if (validImages.Count > 0)
                    {
                        // Pick a random image from the post
                        int randomIndex = Random.Shared.Next(validImages.Count);
                        string selectedImageUrl = validImages[randomIndex];

                        try
                        {
                            byte[] imgBytes = await client.GetByteArrayAsync(selectedImageUrl);
                            string tempImgPath = Path.Combine(Path.GetTempPath(), "ys_post_thumb_" + Environment.TickCount64 + ".png");
                            await File.WriteAllBytesAsync(tempImgPath, imgBytes);
                            downloadedImage = tempImgPath;
                        }
                        catch (Exception ex)
                        {
                            AppLogger.Warning("AutoUpdate", $"Could not download random post image: {selectedImageUrl}", ex);
                        }
                    }

                    // Fall back to launcher logo if no post image is available or download fails
                    if (string.IsNullOrEmpty(downloadedImage) || !File.Exists(downloadedImage))
                    {
                        downloadedImage = GetOrExtractNotificationIconPath();
                    }

                    return (postUrl, postTitle, downloadedImage);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warning("AutoUpdate", "Could not fetch latest post metadata from RSS feed.", ex);
            }

            string? cachedUrl = LauncherConfig.LatestPostUrl;
            string finalUrl = (!string.IsNullOrEmpty(cachedUrl) && Uri.TryCreate(cachedUrl, UriKind.Absolute, out _)) ? cachedUrl : BlogUrl;
            fallbackImage = GetOrExtractNotificationIconPath();
            return (finalUrl, "Yandere Simulator Update", fallbackImage);
        }

        public static async Task<string> FetchLatestPostUrlAsync()
        {
            var info = await FetchLatestPostInfoAsync();
            return info.postUrl;
        }

        public static string GetLatestPostUrl()
        {
            try
            {
                return FetchLatestPostUrlAsync().GetAwaiter().GetResult();
            }
            catch
            {
                string? cached = LauncherConfig.LatestPostUrl;
                if (!string.IsNullOrEmpty(cached) && Uri.TryCreate(cached, UriKind.Absolute, out _))
                {
                    return cached;
                }

                return BlogUrl;
            }
        }

        public static string GetOrExtractNotificationIconPath()
        {
            try
            {
                string configIcon = Path.Combine(ConfigDir, "logo.png");
                if (File.Exists(configIcon) && new FileInfo(configIcon).Length > 0)
                {
                    return configIcon;
                }

                string baseDir = AppContext.BaseDirectory;
                string[] candidatePaths = new[]
                {
                    Path.Combine(baseDir, "Assets", "logo.png"),
                    Path.Combine(baseDir, "logo.png"),
                    Path.Combine(baseDir, "..", "..", "..", "Assets", "logo.png"),
                    Path.Combine(Directory.GetCurrentDirectory(), "Assets", "logo.png"),
                    Path.Combine(Directory.GetCurrentDirectory(), "logo.png")
                };

                foreach (var candidate in candidatePaths)
                {
                    if (File.Exists(candidate) && new FileInfo(candidate).Length > 0)
                    {
                        try
                        {
                            if (!Directory.Exists(ConfigDir)) Directory.CreateDirectory(ConfigDir);
                            File.Copy(candidate, configIcon, true);
                            return configIcon;
                        }
                        catch
                        {
                            return candidate;
                        }
                    }
                }

                string? installPath = LauncherConfig.InstallPath;
                if (!string.IsNullOrWhiteSpace(installPath) && Directory.Exists(installPath))
                {
                    string installLogo = Path.Combine(installPath, "logo.png");
                    if (File.Exists(installLogo) && new FileInfo(installLogo).Length > 0)
                    {
                        try
                        {
                            if (!Directory.Exists(ConfigDir)) Directory.CreateDirectory(ConfigDir);
                            File.Copy(installLogo, configIcon, true);
                            return configIcon;
                        }
                        catch
                        {
                            return installLogo;
                        }
                    }
                }

                try
                {
                    var uri = new Uri("avares://YSLauncher/Assets/logo.png");
                    if (Avalonia.Platform.AssetLoader.Exists(uri))
                    {
                        if (!Directory.Exists(ConfigDir)) Directory.CreateDirectory(ConfigDir);
                        using var s = Avalonia.Platform.AssetLoader.Open(uri);
                        using var fs = File.Create(configIcon);
                        s.CopyTo(fs);
                        return configIcon;
                    }
                }
                catch { }
            }
            catch (Exception ex)
            {
                AppLogger.Warning("AutoUpdate", "Could not get or extract notification icon.", ex);
            }

            return Path.Combine(ConfigDir, "logo.png");
        }

        public static string GetOrExtractNotificationIcoPath()
        {
            try
            {
                string configIco = Path.Combine(ConfigDir, "icon.ico");
                if (File.Exists(configIco) && new FileInfo(configIco).Length > 0)
                {
                    return configIco;
                }

                string baseDir = AppContext.BaseDirectory;
                string[] candidatePaths = new[]
                {
                    Path.Combine(baseDir, "Assets", "icon.ico"),
                    Path.Combine(baseDir, "icon.ico"),
                    Path.Combine(baseDir, "..", "..", "..", "Assets", "icon.ico"),
                    Path.Combine(Directory.GetCurrentDirectory(), "Assets", "icon.ico")
                };

                foreach (var candidate in candidatePaths)
                {
                    if (File.Exists(candidate) && new FileInfo(candidate).Length > 0)
                    {
                        try
                        {
                            if (!Directory.Exists(ConfigDir)) Directory.CreateDirectory(ConfigDir);
                            File.Copy(candidate, configIco, true);
                            return configIco;
                        }
                        catch
                        {
                            return candidate;
                        }
                    }
                }

                try
                {
                    var uri = new Uri("avares://YSLauncher/Assets/icon.ico");
                    if (Avalonia.Platform.AssetLoader.Exists(uri))
                    {
                        if (!Directory.Exists(ConfigDir)) Directory.CreateDirectory(ConfigDir);
                        using var s = Avalonia.Platform.AssetLoader.Open(uri);
                        using var fs = File.Create(configIco);
                        s.CopyTo(fs);
                        return configIco;
                    }
                }
                catch { }
            }
            catch (Exception ex)
            {
                AppLogger.Warning("AutoUpdate", "Could not get or extract notification ico.", ex);
            }

            return Path.Combine(ConfigDir, "icon.ico");
        }

        public static void EnsureLinuxDesktopAndIcons(string iconPath, string exePath)
        {
            if (!OperatingSystem.IsLinux()) return;
            try
            {
                string userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (string.IsNullOrEmpty(userHome)) return;

                if (File.Exists(iconPath))
                {
                    string iconsAppDir = Path.Combine(userHome, ".local", "share", "icons", "hicolor", "256x256", "apps");
                    string pixmapsDir = Path.Combine(userHome, ".local", "share", "pixmaps");

                    if (!Directory.Exists(iconsAppDir)) Directory.CreateDirectory(iconsAppDir);
                    if (!Directory.Exists(pixmapsDir)) Directory.CreateDirectory(pixmapsDir);

                    string targetIcon1 = Path.Combine(iconsAppDir, "yslauncher.png");
                    string targetIcon2 = Path.Combine(pixmapsDir, "yslauncher.png");

                    if (!File.Exists(targetIcon1)) File.Copy(iconPath, targetIcon1, true);
                    if (!File.Exists(targetIcon2)) File.Copy(iconPath, targetIcon2, true);
                }

                string appsDir = Path.Combine(userHome, ".local", "share", "applications");
                string desktopFile = Path.Combine(appsDir, "yslauncher.desktop");
                if (!File.Exists(desktopFile))
                {
                    if (!Directory.Exists(appsDir)) Directory.CreateDirectory(appsDir);
                    string targetExe = !string.IsNullOrEmpty(exePath) && File.Exists(exePath) ? exePath : "YSLauncher";
                    string targetIcon = File.Exists(iconPath) ? iconPath : "yslauncher";
                    string content = $"[Desktop Entry]\nType=Application\nName=YSLauncher\nComment=Launcher para Yandere Simulator\nExec=\"{targetExe}\"\nIcon={targetIcon}\nTerminal=false\nCategories=Game;\n";
                    File.WriteAllText(desktopFile, content);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warning("AutoUpdate", "Could not ensure Linux desktop entries and icons.", ex);
            }
        }

        public static bool IsAutoUpdateEnabled()
        {
            LauncherConfig.Reload();
            return LauncherConfig.AutoUpdate && LauncherConfig.HasConfiguredLanguage;
        }

        public static bool HasPendingUpdate()
        {
            LauncherConfig.Reload();
            return LauncherConfig.HasPendingUpdate;
        }

        public static void ConsumePendingUpdate()
        {
            LauncherConfig.ConsumePendingUpdate();
        }

        public static TimeZoneInfo GetPacificTimeZone()
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles"); } catch { }
            try { return TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time"); } catch { }
            
            foreach (var tz in TimeZoneInfo.GetSystemTimeZones())
            {
                if (tz.Id.Contains("Los_Angeles", StringComparison.OrdinalIgnoreCase) ||
                    tz.Id.Contains("Pacific", StringComparison.OrdinalIgnoreCase))
                {
                    return tz;
                }
            }
            return TimeZoneInfo.Utc;
        }

        public static bool IsReleaseWindow(DateTime utcNow)
        {
            try
            {
                var laTz = GetPacificTimeZone();
                DateTime laTime = TimeZoneInfo.ConvertTimeFromUtc(utcNow, laTz);

                int day = laTime.Day;
                int hour = laTime.Hour;

                // Day 1 Window (end of previous month 20:00 -> day 1 all day -> day 2 08:00)
                int daysInPrevMonth = DateTime.DaysInMonth(
                    laTime.Month == 1 ? laTime.Year - 1 : laTime.Year,
                    laTime.Month == 1 ? 12 : laTime.Month - 1
                );
                if (day == daysInPrevMonth && hour >= 20) return true;
                if (day == 1) return true;
                if (day == 2 && hour < 8) return true;

                // Day 15 Window (day 14 20:00 -> day 15 all day -> day 16 08:00)
                if (day == 14 && hour >= 20) return true;
                if (day == 15) return true;
                if (day == 16 && hour < 8) return true;
            }
            catch (Exception ex)
            {
                AppLogger.Warning("AutoUpdate", "Failed to check release window, falling back to false.", ex);
            }

            return false;
        }

        public static TimeSpan GetCheckInterval(DateTime utcNow)
        {
            // During YandereDev release windows (1st & 15th): check every 15 minutes.
            // On normal days: check every 4 hours.
            return IsReleaseWindow(utcNow) ? TimeSpan.FromMinutes(15) : TimeSpan.FromHours(4);
        }

        public static string GetLauncherExecutablePath()
        {
            try
            {
                if (OperatingSystem.IsMacOS())
                {
                    string defaultAppExe = "/Applications/YSLauncher.app/Contents/MacOS/YSLauncher";
                    if (File.Exists(defaultAppExe)) return defaultAppExe;
                }
                else if (OperatingSystem.IsLinux())
                {
                    string userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    string installedExe = Path.Combine(userHome, ".local", "share", "YSLauncher", "YSLauncher");
                    if (File.Exists(installedExe)) return installedExe;

                    string localBin = Path.Combine(userHome, ".local", "bin", "YSLauncher");
                    if (File.Exists(localBin)) return localBin;
                }

                string? current = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(current) && File.Exists(current))
                {
                    string fname = Path.GetFileName(current).ToLowerInvariant();
                    if (fname != "dotnet" && fname != "dotnet.exe")
                    {
                        return current;
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warning("AutoUpdate", "Could not determine launcher executable path.", ex);
            }

            return "YSLauncher";
        }

        public static async Task RunHeadlessCheckAsync()
        {
            try
            {
                LauncherConfig.Reload();
                if (!LauncherConfig.HasConfiguredLanguage)
                {
                    AppLogger.Log("AutoUpdate", "Language has not been configured yet. Skipping headless check.");
                    return;
                }

                I18n.AutoDetect();
                AppLogger.Log("AutoUpdate", "Running headless background update check...");

                if (!IsAutoUpdateEnabled())
                {
                    AppLogger.Log("AutoUpdate", "Auto-update is disabled. Unregistering scheduler...");
                    UnregisterScheduler();
                    return;
                }

                string? installPath = LauncherConfig.InstallPath;
                if (string.IsNullOrEmpty(installPath) || !Directory.Exists(installPath))
                {
                    AppLogger.Log("AutoUpdate", $"Install directory '{installPath}' does not exist. Skipping check.");
                    return;
                }

                DateTime utcNow = DateTime.UtcNow;
                string? lastCheckStr = LauncherConfig.LastCheckTime;
                if (!string.IsNullOrEmpty(lastCheckStr))
                {
                    if (DateTime.TryParse(lastCheckStr, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out DateTime lastCheck))
                    {
                        TimeSpan requiredInterval = GetCheckInterval(utcNow);
                        TimeSpan elapsed = utcNow - lastCheck;
                        if (elapsed < requiredInterval && elapsed >= TimeSpan.Zero)
                        {
                            AppLogger.Log("AutoUpdate", $"Update check skipped (elapsed: {elapsed:hh\\:mm\\:ss}, required: {requiredInterval:hh\\:mm\\:ss}).");
                            return;
                        }
                    }
                }

                LauncherConfig.LastCheckTime = utcNow.ToString("O");

                GameUpdater updater = new();
                if (!updater.IsGameInstalled(installPath, "Latest"))
                {
                    AppLogger.Log("AutoUpdate", "Game is not installed in Latest. Skipping check.");
                    return;
                }

                var checkResult = await updater.CheckForUpdatesAsync(installPath);
                AppLogger.Log("AutoUpdate", $"Check result: {checkResult}");

                if (checkResult == UpdateCheckResult.UpdateAvailable)
                {
                    string buildId = updater.CachedDirectUrl ?? updater.CachedRemoteSizeBytes.ToString();
                    if (string.IsNullOrEmpty(buildId) || buildId == "-1")
                    {
                        buildId = utcNow.ToString("yyyy-MM-dd");
                    }

                    string? lastNotified = LauncherConfig.LastNotifiedBuild;
                    if (lastNotified == buildId)
                    {
                        AppLogger.Log("AutoUpdate", $"Build '{buildId}' was already notified. Skipping notification.");
                        return;
                    }

                    LauncherConfig.LastNotifiedBuild = buildId;
                    LauncherConfig.HasPendingUpdate = true;

                    string postUrl = await FetchLatestPostUrlAsync();
                    AppLogger.Log("AutoUpdate", $"New update detected! Build ID: {buildId}, Latest post URL: {postUrl}. Triggering notification...");
                    NotifyUpdateAvailable(postUrl);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("AutoUpdate", "Failed during headless update check.", ex);
            }
        }

        public static void NotifyUpdateAvailable(string? postUrl = null)
        {
            try
            {
                var postInfo = FetchLatestPostInfoAsync().GetAwaiter().GetResult();
                string finalPostUrl = !string.IsNullOrEmpty(postUrl) ? postUrl : postInfo.postUrl;
                string postTitle = !string.IsNullOrEmpty(postInfo.postTitle) ? postInfo.postTitle : I18n.T("UpdateNotificationTitle");
                string? imagePath = postInfo.imagePath;

                string title = I18n.T("UpdateNotificationTitle");
                string body = I18n.T("UpdateNotificationBody");
                string exePath = GetLauncherExecutablePath();

                if (OperatingSystem.IsMacOS())
                {
                    ShowMacNotification(title, body, exePath, finalPostUrl, postTitle);
                }
                else if (OperatingSystem.IsWindows())
                {
                    ShowWindowsNotification(title, body, exePath, finalPostUrl, postTitle, imagePath);
                }
                else if (OperatingSystem.IsLinux())
                {
                    ShowLinuxNotification(title, body, exePath, finalPostUrl, postTitle, imagePath);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("AutoUpdate", "Failed to dispatch notification.", ex);
            }
        }

        private static void ShowMacNotification(string title, string body, string exePath, string postUrl, string postTitle)
        {
            try
            {
                string escapedTitle = title.Replace("\"", "\\\"");
                string escapedSubtitle = postTitle.Replace("\"", "\\\"");
                string escapedBody = body.Replace("\"", "\\\"");
                string bannerScript = $"display notification \"{escapedBody}\" with title \"{escapedTitle}\" subtitle \"{escapedSubtitle}\" sound name \"default\"";
                var bannerPsi = new ProcessStartInfo
                {
                    FileName = "osascript",
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                bannerPsi.ArgumentList.Add("-e");
                bannerPsi.ArgumentList.Add(bannerScript);
                Process.Start(bannerPsi);
            }
            catch (Exception ex)
            {
                AppLogger.Warning("AutoUpdate", "Could not show macOS banner notification.", ex);
            }
        }

        private static void ShowWindowsNotification(string title, string body, string exePath, string postUrl, string postTitle, string? imagePath)
        {
            try
            {
                string targetUrl = string.IsNullOrEmpty(postUrl) ? BlogUrl : postUrl;
                string readPostBtn = I18n.T("UpdateNotificationReadPost");
                string openLauncherBtn = I18n.T("UpdateNotificationOpenLauncher");

                string effectiveImgPath = (!string.IsNullOrEmpty(imagePath) && File.Exists(imagePath))
                    ? Path.GetFullPath(imagePath)
                    : Path.GetFullPath(GetOrExtractNotificationIconPath());

                // Construct local file URI for Windows Toast XML payload
                string imgUri = new Uri(effectiveImgPath).AbsoluteUri;

                string escapedTitle = System.Security.SecurityElement.Escape(title);
                string escapedPostTitle = System.Security.SecurityElement.Escape(postTitle);
                string escapedBody = System.Security.SecurityElement.Escape(body);
                string escapedPostBtn = System.Security.SecurityElement.Escape(readPostBtn);
                string escapedLauncherBtn = System.Security.SecurityElement.Escape(openLauncherBtn);

                string escapedExe = exePath.Replace("'", "''");
                string escapedPostUrl = targetUrl.Replace("'", "''");

                // Native Windows 10/11 Toast notification template with hero image
                string toastXml = $@"<toast activationType='protocol' launch='{targetUrl}'>
    <visual>
        <binding template='ToastGeneric'>
            <text>{escapedTitle}</text>
            <text>{escapedPostTitle}</text>
            <text>{escapedBody}</text>
            <image placement='hero' src='{imgUri}'/>
        </binding>
    </visual>
    <actions>
        <action content='{escapedPostBtn}' arguments='{targetUrl}' activationType='protocol'/>
        <action content='{escapedLauncherBtn}' arguments='yslauncher://launch' activationType='protocol'/>
    </actions>
</toast>";

                string escapedXml = toastXml.Replace("'", "''");

                string psScript = $@"
$appId = 'YandereSimulator.Launcher'

# Create Start Menu shortcut with AppUserModelId to ensure Toast delivery on Windows 10/11
$startMenuDir = [System.IO.Path]::Combine($env:APPDATA, 'Microsoft\Windows\Start Menu\Programs')
$shortcutPath = [System.IO.Path]::Combine($startMenuDir, 'YSLauncher.lnk')

try {{
    if (-not (Test-Path $shortcutPath)) {{
        $wsh = New-Object -ComObject WScript.Shell
        $shortcut = $wsh.CreateShortcut($shortcutPath)
        $shortcut.TargetPath = '{escapedExe}'
        $shortcut.Save()
    }}

    $regPath = 'HKCU:\Software\Classes\AppUserModelId\' + $appId
    if (-not (Test-Path $regPath)) {{
        New-Item -Path $regPath -Force | Out-Null
        Set-ItemProperty -Path $regPath -Name 'DisplayName' -Value 'YSLauncher' -Force
    }}

    # Register custom yslauncher:// URI scheme for protocol activation
    $protoReg = 'HKCU:\Software\Classes\yslauncher'
    if (-not (Test-Path $protoReg)) {{
        New-Item -Path $protoReg -Force | Out-Null
        Set-ItemProperty -Path $protoReg -Name '(Default)' -Value 'URL:YSLauncher Protocol' -Force
        Set-ItemProperty -Path $protoReg -Name 'URL Protocol' -Value '' -Force
    }}
    $cmdReg = 'HKCU:\Software\Classes\yslauncher\shell\open\command'
    if (-not (Test-Path $cmdReg)) {{
        New-Item -Path $cmdReg -Force | Out-Null
    }}
    Set-ItemProperty -Path $cmdReg -Name '(Default)' -Value '""{escapedExe}"" ""%1""' -Force
}} catch {{}}

# Dispara o Toast Nativo do Windows 10/11
[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null
[Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType = WindowsRuntime] | Out-Null

$xmlDoc = New-Object Windows.Data.Xml.Dom.XmlDocument
$xmlDoc.LoadXml(@'
{toastXml}
'@)

$toast = New-Object Windows.UI.Notifications.ToastNotification($xmlDoc)
$notifier = [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier($appId)
$notifier.Show($toast)
";

                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                psi.ArgumentList.Add("-WindowStyle");
                psi.ArgumentList.Add("Hidden");
                psi.ArgumentList.Add("-NoProfile");
                psi.ArgumentList.Add("-NonInteractive");
                psi.ArgumentList.Add("-Command");
                psi.ArgumentList.Add(psScript);
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                AppLogger.Warning("AutoUpdate", "Could not show Windows Toast notification.", ex);
            }
        }

        private static void ShowLinuxNotification(string title, string body, string exePath, string postUrl, string postTitle, string? imagePath)
        {
            try
            {
                string targetUrl = string.IsNullOrEmpty(postUrl) ? BlogUrl : postUrl;
                string readPostBtn = I18n.T("UpdateNotificationReadPost");
                string openLauncherBtn = I18n.T("UpdateNotificationOpenLauncher");

                string iconPath = GetOrExtractNotificationIconPath();
                EnsureLinuxDesktopAndIcons(iconPath, exePath);

                string combinedBody = !string.IsNullOrWhiteSpace(postTitle) ? $"{postTitle}\n{body}" : body;
                string effectiveImage = (!string.IsNullOrEmpty(imagePath) && File.Exists(imagePath)) ? imagePath : "";

                string runnerScript = """
#!/bin/bash

title="$1"
body="$2"
url="$3"
exe="$4"
btn_post="$5"
btn_launch="$6"
icon="$7"
img="$8"

open_post() {
    if command -v systemd-run >/dev/null 2>&1; then
        systemd-run --user xdg-open "$url" >/dev/null 2>&1
    elif command -v setsid >/dev/null 2>&1; then
        setsid xdg-open "$url" >/dev/null 2>&1 &
    else
        nohup xdg-open "$url" >/dev/null 2>&1 &
    fi
}

open_launcher() {
    if command -v systemd-run >/dev/null 2>&1; then
        systemd-run --user "$exe" >/dev/null 2>&1
    elif command -v setsid >/dev/null 2>&1; then
        setsid "$exe" >/dev/null 2>&1 &
    else
        nohup "$exe" >/dev/null 2>&1 &
    fi
}

# 1. Native notify-send if action support is present (libnotify >= 0.8.0)
if command -v notify-send >/dev/null 2>&1 && notify-send --help 2>&1 | grep -q -- '--action'; then
    cmd=(notify-send -a "YSLauncher" -h "string:desktop-entry:yslauncher")
    [ -n "$icon" ] && [ -f "$icon" ] && cmd+=(-i "$icon")
    [ -n "$img" ] && [ -f "$img" ] && cmd+=(-h "string:image-path:$img")
    cmd+=("$title" "$body" --action="post=$btn_post" --action="launcher=$btn_launch")

    action=$("${cmd[@]}" 2>/dev/null)
    if [ $? -eq 0 ]; then
        if [ "$action" = "post" ]; then
            open_post
        elif [ "$action" = "launcher" ]; then
            open_launcher
        fi
        exit 0
    fi
fi

# 2. Universal Freedesktop D-Bus via Python 3 + gdbus (Ubuntu 20.04/22.04 LTS and GNOME/KDE)
if command -v python3 >/dev/null 2>&1 && command -v gdbus >/dev/null 2>&1; then
    python3 - << 'PYEOF' "$title" "$body" "$url" "$exe" "$btn_post" "$btn_launch" "$icon" "$img"
import sys, os, subprocess, re, select, time

title, body, post_url, exe_path, btn_post, btn_launch, icon_path, img_path = sys.argv[1:9]

mon = None
if subprocess.run(['which', 'dbus-monitor'], capture_output=True).returncode == 0:
    try:
        mon = subprocess.Popen(
            ['dbus-monitor', "type='signal',interface='org.freedesktop.Notifications'"],
            stdout=subprocess.PIPE,
            stderr=subprocess.DEVNULL,
            text=True,
            bufsize=1
        )
    except Exception:
        mon = None

def esc(s):
    return s.replace('\\', '\\\\').replace("'", "\\'")

actions = f"['post', '{esc(btn_post)}', 'launcher', '{esc(btn_launch)}']"
hints = "{'desktop-entry': <'yslauncher'>}"
if img_path and os.path.isfile(img_path):
    hints = f"{{'desktop-entry': <'yslauncher'>, 'image-path': <'{esc(img_path)}'>}}"

icon_arg = icon_path if (icon_path and os.path.isfile(icon_path)) else 'yslauncher'

cmd = [
    'gdbus', 'call', '--session',
    '--dest', 'org.freedesktop.Notifications',
    '--object-path', '/org/freedesktop/Notifications',
    '--method', 'org.freedesktop.Notifications.Notify',
    'YSLauncher', '0', icon_arg,
    title, body, actions, hints, '20000'
]

res = subprocess.run(cmd, capture_output=True, text=True)
m = re.search(r'uint32\s+(\d+)', res.stdout)

if mon and m:
    nid = m.group(1)
    start = time.time()
    state = ''
    while time.time() - start < 25:
        r, _, _ = select.select([mon.stdout], [], [], 1.0)
        if not r:
            continue
        line = mon.stdout.readline()
        if not line:
            break
        if 'member=ActionInvoked' in line:
            state = 'action'
        elif 'member=NotificationClosed' in line:
            state = 'closed'
        elif state == 'action':
            if f'uint32 {nid}' in line:
                next_line = mon.stdout.readline()
                m_act = re.search(r'string\s+"([^"]+)"', next_line)
                if m_act:
                    action = m_act.group(1)
                    if action == 'post':
                        subprocess.Popen(['xdg-open', post_url])
                    elif action == 'launcher':
                        subprocess.Popen([exe_path])
                    break
            else:
                state = ''
        elif state == 'closed':
            if f'uint32 {nid}' in line:
                break
            else:
                state = ''
    try:
        mon.terminate()
    except Exception:
        pass
    sys.exit(0)
PYEOF
    if [ $? -eq 0 ]; then
        exit 0
    fi
fi

# 3. Fallback: simple notify-send without actions
if command -v notify-send >/dev/null 2>&1; then
    fallback_cmd=(notify-send -a "YSLauncher" -h "string:desktop-entry:yslauncher")
    [ -n "$icon" ] && [ -f "$icon" ] && fallback_cmd+=(-i "$icon")
    [ -n "$img" ] && [ -f "$img" ] && fallback_cmd+=(-h "string:image-path:$img")
    fallback_cmd+=("$title" "$body")
    "${fallback_cmd[@]}" >/dev/null 2>&1 &
    exit 0
fi
""";

                string scriptPath = Path.Combine(ConfigDir, "notify_handler.sh");
                File.WriteAllText(scriptPath, runnerScript);
                try
                {
                    if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                    {
                        File.SetUnixFileMode(scriptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                    }
                }
                catch { }

                var psi = new ProcessStartInfo
                {
                    FileName = "/bin/bash",
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                psi.ArgumentList.Add(scriptPath);
                psi.ArgumentList.Add(title);
                psi.ArgumentList.Add(combinedBody);
                psi.ArgumentList.Add(targetUrl);
                psi.ArgumentList.Add(exePath);
                psi.ArgumentList.Add(readPostBtn);
                psi.ArgumentList.Add(openLauncherBtn);
                psi.ArgumentList.Add(iconPath);
                psi.ArgumentList.Add(effectiveImage);

                string? dbus = Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS");
                if (!string.IsNullOrEmpty(dbus)) psi.EnvironmentVariables["DBUS_SESSION_BUS_ADDRESS"] = dbus;
                string? display = Environment.GetEnvironmentVariable("DISPLAY");
                if (!string.IsNullOrEmpty(display)) psi.EnvironmentVariables["DISPLAY"] = display;
                string? wayland = Environment.GetEnvironmentVariable("WAYLAND_DISPLAY");
                if (!string.IsNullOrEmpty(wayland)) psi.EnvironmentVariables["WAYLAND_DISPLAY"] = wayland;
                string? xauth = Environment.GetEnvironmentVariable("XAUTHORITY");
                if (!string.IsNullOrEmpty(xauth)) psi.EnvironmentVariables["XAUTHORITY"] = xauth;

                Process.Start(psi);
            }
            catch (Exception ex)
            {
                AppLogger.Warning("AutoUpdate", "Could not show Linux notification.", ex);
            }
        }

        public static void OpenBrowser(string url)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(url)) return;

                if (OperatingSystem.IsWindows())
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = url,
                        UseShellExecute = true
                    });
                }
                else if (OperatingSystem.IsMacOS())
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "open",
                        ArgumentList = { url },
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });
                }
                else
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "xdg-open",
                        ArgumentList = { url },
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warning("Browser", $"Failed to open browser for {url}", ex);
            }
        }

        public static void RegisterScheduler()
        {
            try
            {
                LauncherConfig.Reload();
                if (!LauncherConfig.HasConfiguredLanguage)
                {
                    AppLogger.Log("AutoUpdate", "Cannot register scheduler: user has not configured language yet.");
                    return;
                }

                string exePath = GetLauncherExecutablePath();
                AppLogger.Log("AutoUpdate", $"Registering scheduler with executable: '{exePath}'");

                if (OperatingSystem.IsMacOS())
                {
                    RegisterMacScheduler(exePath);
                }
                else if (OperatingSystem.IsWindows())
                {
                    RegisterWindowsScheduler(exePath);
                }
                else if (OperatingSystem.IsLinux())
                {
                    RegisterLinuxScheduler(exePath);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("AutoUpdate", "Failed to register OS scheduler.", ex);
            }
        }

        public static void UnregisterScheduler()
        {
            try
            {
                AppLogger.Log("AutoUpdate", "Unregistering OS update scheduler...");

                if (OperatingSystem.IsMacOS())
                {
                    UnregisterMacScheduler();
                }
                else if (OperatingSystem.IsWindows())
                {
                    UnregisterWindowsScheduler();
                }
                else if (OperatingSystem.IsLinux())
                {
                    UnregisterLinuxScheduler();
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("AutoUpdate", "Failed to unregister OS scheduler.", ex);
            }
        }

        private static void RegisterMacScheduler(string exePath)
        {
            try
            {
                string userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string launchAgentsDir = Path.Combine(userHome, "Library", "LaunchAgents");
                if (!Directory.Exists(launchAgentsDir))
                {
                    Directory.CreateDirectory(launchAgentsDir);
                }

                string plistPath = Path.Combine(launchAgentsDir, "com.yanderesimulator.launcher.updater.plist");
                string plistContent = $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<!DOCTYPE plist PUBLIC ""-//Apple//DTD PLIST 1.0//EN"" ""http://www.apple.com/DTDs/PropertyList-1.0.dtd"">
<plist version=""1.0"">
<dict>
    <key>Label</key>
    <string>com.yanderesimulator.launcher.updater</string>
    <key>ProcessType</key>
    <string>Background</string>
    <key>ProgramArguments</key>
    <array>
        <string>{exePath}</string>
        <string>--check-updates</string>
    </array>
    <key>StartInterval</key>
    <integer>900</integer>
    <key>RunAtLoad</key>
    <false/>
</dict>
</plist>";

                File.WriteAllText(plistPath, plistContent);

                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "launchctl",
                        Arguments = $"unload \"{plistPath}\"",
                        CreateNoWindow = true,
                        UseShellExecute = false
                    })?.WaitForExit(3000);
                }
                catch { }

                Process.Start(new ProcessStartInfo
                {
                    FileName = "launchctl",
                    Arguments = $"load \"{plistPath}\"",
                    CreateNoWindow = true,
                    UseShellExecute = false
                })?.WaitForExit(3000);

                AppLogger.Log("AutoUpdate", $"macOS LaunchAgent registered successfully at '{plistPath}'.");
            }
            catch (Exception ex)
            {
                AppLogger.Error("AutoUpdate", "Failed to register macOS LaunchAgent.", ex);
            }
        }

        private static void UnregisterMacScheduler()
        {
            try
            {
                string userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string plistPath = Path.Combine(userHome, "Library", "LaunchAgents", "com.yanderesimulator.launcher.updater.plist");

                if (File.Exists(plistPath))
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = "launchctl",
                            Arguments = $"unload \"{plistPath}\"",
                            CreateNoWindow = true,
                            UseShellExecute = false
                        })?.WaitForExit(3000);
                    }
                    catch { }

                    File.Delete(plistPath);
                    AppLogger.Log("AutoUpdate", $"macOS LaunchAgent removed: '{plistPath}'.");
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("AutoUpdate", "Failed to unregister macOS LaunchAgent.", ex);
            }
        }

        private static void RegisterWindowsScheduler(string exePath)
        {
            try
            {
                string args = $"/create /tn \"YSLauncherUpdateCheck\" /tr \"\\\"{exePath}\\\" --check-updates\" /sc minute /mo 15 /f";
                Process.Start(new ProcessStartInfo
                {
                    FileName = "schtasks.exe",
                    Arguments = args,
                    CreateNoWindow = true,
                    UseShellExecute = false
                })?.WaitForExit(5000);

                AppLogger.Log("AutoUpdate", "Windows scheduled task 'YSLauncherUpdateCheck' created successfully.");
            }
            catch (Exception ex)
            {
                AppLogger.Error("AutoUpdate", "Failed to register Windows scheduled task.", ex);
            }
        }

        private static void UnregisterWindowsScheduler()
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "schtasks.exe",
                    Arguments = "/delete /tn \"YSLauncherUpdateCheck\" /f",
                    CreateNoWindow = true,
                    UseShellExecute = false
                })?.WaitForExit(5000);

                AppLogger.Log("AutoUpdate", "Windows scheduled task 'YSLauncherUpdateCheck' removed.");
            }
            catch (Exception ex)
            {
                AppLogger.Error("AutoUpdate", "Failed to unregister Windows scheduled task.", ex);
            }
        }

        private static void RegisterLinuxScheduler(string exePath)
        {
            try
            {
                string userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string systemdUserDir = Path.Combine(userHome, ".config", "systemd", "user");
                if (!Directory.Exists(systemdUserDir))
                {
                    Directory.CreateDirectory(systemdUserDir);
                }

                string servicePath = Path.Combine(systemdUserDir, "yslauncher-updater.service");
                string serviceContent = $@"[Unit]
Description=YSLauncher Update Check

[Service]
Type=oneshot
ExecStart=""{exePath}"" --check-updates
";
                string timerPath = Path.Combine(systemdUserDir, "yslauncher-updater.timer");
                string timerContent = $@"[Unit]
Description=YSLauncher Update Check Timer

[Timer]
OnBootSec=2min
OnUnitActiveSec=15min

[Install]
WantedBy=timers.target
";
                bool serviceExists = File.Exists(servicePath);
                bool timerExists = File.Exists(timerPath);
                bool alreadyMatches = serviceExists && timerExists &&
                                      File.ReadAllText(servicePath) == serviceContent &&
                                      File.ReadAllText(timerPath) == timerContent;

                if (!alreadyMatches)
                {
                    File.WriteAllText(servicePath, serviceContent);
                    File.WriteAllText(timerPath, timerContent);

                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "systemctl",
                        Arguments = "--user daemon-reload",
                        CreateNoWindow = true,
                        UseShellExecute = false
                    })?.WaitForExit(3000);

                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "systemctl",
                        Arguments = "--user enable --now yslauncher-updater.timer",
                        CreateNoWindow = true,
                        UseShellExecute = false
                    })?.WaitForExit(3000);

                    AppLogger.Log("AutoUpdate", "Linux systemd user timer 'yslauncher-updater.timer' enabled.");
                }
                else
                {
                    AppLogger.Log("AutoUpdate", "Linux systemd timer already up to date.");
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("AutoUpdate", "Failed to register Linux systemd timer.", ex);
            }
        }

        private static void UnregisterLinuxScheduler()
        {
            try
            {
                string userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string systemdUserDir = Path.Combine(userHome, ".config", "systemd", "user");

                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "systemctl",
                        Arguments = "--user disable --now yslauncher-updater.timer",
                        CreateNoWindow = true,
                        UseShellExecute = false
                    })?.WaitForExit(3000);
                }
                catch { }

                string servicePath = Path.Combine(systemdUserDir, "yslauncher-updater.service");
                string timerPath = Path.Combine(systemdUserDir, "yslauncher-updater.timer");
                if (File.Exists(servicePath)) File.Delete(servicePath);
                if (File.Exists(timerPath)) File.Delete(timerPath);

                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "systemctl",
                        Arguments = "--user daemon-reload",
                        CreateNoWindow = true,
                        UseShellExecute = false
                    })?.WaitForExit(3000);
                }
                catch { }

                AppLogger.Log("AutoUpdate", "Linux systemd timer unregistered.");
            }
            catch (Exception ex)
            {
                AppLogger.Error("AutoUpdate", "Failed to unregister Linux systemd timer.", ex);
            }
        }
    }
