using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Cryptography;
using System.Text;
using SharpCompress.Archives;
using SharpCompress.Archives.Rar;
using YSLauncher.Platforms.Linux;

namespace YSLauncher.Services;
    public enum UpdateCheckResult
    {
        UpToDate,
        UpdateAvailable,
        NotInstalled,
        NotSupported,
        Failed
    }

    public class GameUpdater
    {
        private static readonly HttpClient client = CreateHttpClient();
        private const string WP_URL = "https://yanderesimulator.com/download/";
        private const string GameFolderName = "YandereSimulator";
        private const string ExecutableName = "YandereSimulator.exe";

        public event EventHandler<string>? StatusChanged;
        public event EventHandler<int>? ProgressChanged;
        public event EventHandler? DownloadCompleted;

        public string? CachedDirectUrl { get; private set; }
        public long CachedRemoteSizeBytes { get; private set; } = -1;
        public string? LastError { get; set; }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime dirWrite, DateTime? date)> _buildDateCache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime pathWrite, DateTime versWrite, Dictionary<string, string> map)> _installedMapCache = new(StringComparer.OrdinalIgnoreCase);

        private static HttpClient CreateHttpClient()
        {
            var httpClient = new HttpClient(new HttpClientHandler
            {
                AutomaticDecompression = System.Net.DecompressionMethods.All
            });
            
            if (OperatingSystem.IsMacOS())
            {
                httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.5 Safari/605.1.15");
            }
            else
            {
                httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36");
            }

            httpClient.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
            httpClient.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9,pt-BR;q=0.8,pt;q=0.7");
            httpClient.DefaultRequestHeaders.Add("Sec-Fetch-Dest", "document");
            httpClient.DefaultRequestHeaders.Add("Sec-Fetch-Mode", "navigate");
            httpClient.DefaultRequestHeaders.Add("Sec-Fetch-Site", "none");
            httpClient.DefaultRequestHeaders.Add("Sec-Fetch-User", "?1");
            httpClient.DefaultRequestHeaders.Add("Upgrade-Insecure-Requests", "1");
            return httpClient;
        }

        private static async Task<string> GetStringWithTimeoutAsync(string url, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
            try
            {
                return await client.GetStringAsync(url, linkedCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"The request to '{url}' timed out after {timeout.TotalSeconds:0} seconds.");
            }
        }

        private static async Task<HttpResponseMessage> GetAsyncWithTimeoutAsync(string url, HttpCompletionOption completionOption, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
            try
            {
                return await client.GetAsync(url, completionOption, linkedCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"The request to '{url}' timed out after {timeout.TotalSeconds:0} seconds.");
            }
        }

        public static (int year, int month, int day) ParseDateFromVersion(string version)
        {
            var matchDigits = Regex.Match(version, @"(\d{4})[_-](\d{2})[_-](\d{2})");
            if (matchDigits.Success)
            {
                if (int.TryParse(matchDigits.Groups[1].Value, out int y) &&
                    int.TryParse(matchDigits.Groups[2].Value, out int m) &&
                    int.TryParse(matchDigits.Groups[3].Value, out int d))
                {
                    return (y, m, d);
                }
            }

            var matchWords = Regex.Match(version, @"(January|February|Februrary|Feburary|March|April|May|June|July|August|September|October|November|December)\s*(\d{1,2})(?:st|nd|rd|th)?(?:\s*-\s*[A-Za-z])?(?:\s*(\d{4}))?", RegexOptions.IgnoreCase);
            if (matchWords.Success)
            {
                string mName = matchWords.Groups[1].Value.ToLower();
                int d = int.Parse(matchWords.Groups[2].Value);
                int y = matchWords.Groups[3].Success ? int.Parse(matchWords.Groups[3].Value) : 0;

                int m = mName switch
                {
                    "january" => 1,
                    "february" or "februrary" or "feburary" => 2,
                    "march" => 3,
                    "april" => 4,
                    "may" => 5,
                    "june" => 6,
                    "july" => 7,
                    "august" => 8,
                    "september" => 9,
                    "october" => 10,
                    "november" => 11,
                    "december" => 12,
                    _ => 0
                };

                if (m > 0 && d >= 1 && d <= 31) return (y, m, d);
            }

            return (0, 0, 0);
        }

        public static string GetVersionExtra(string name)
        {
            var mLetter = Regex.Match(name, @"-\s*([A-Za-z])(?:\.|\s|$)");
            if (mLetter.Success) return mLetter.Groups[1].Value.ToUpper();

            var mV = Regex.Match(name, @"(v\d+)", RegexOptions.IgnoreCase);
            if (mV.Success) return mV.Groups[1].Value.ToLower();

            return "";
        }

        public async Task<List<string>> FetchArchiveVersionsAsync(bool forceRefresh = false)
        {
            string cacheFile = LauncherConfig.ArchiveVersionsCacheFile;
            if (!Directory.Exists(LauncherConfig.ConfigDir)) Directory.CreateDirectory(LauncherConfig.ConfigDir);

            if (!forceRefresh && File.Exists(cacheFile))
            {
                try
                {
                    var lines = await File.ReadAllLinesAsync(cacheFile);
                    var cachedList = lines.Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
                    if (cachedList.Count > 0)
                    {
                        var lastWrite = File.GetLastWriteTime(cacheFile);
                        if (DateTime.Now - lastWrite < TimeSpan.FromHours(24))
                        {
                            return cachedList;
                        }
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Warning("Catalog", "Could not read the fresh version catalog cache.", ex);
                }
            }

            var versions = new List<string>();
            try
            {
                var taskMain = GetStringWithTimeoutAsync("https://archive.org/download/yandere-simulator-builds/yandere-simulator-builds_files.xml", TimeSpan.FromSeconds(15));
                var task2026 = GetStringWithTimeoutAsync("https://archive.org/download/yandere-simulator-2026-archive/yandere-simulator-2026-archive_files.xml", TimeSpan.FromSeconds(15));

                await Task.WhenAll(taskMain, task2026);

                string xmlMain = await taskMain;
                string xml2026 = await task2026;

                var list = new List<string>();
                var seenKeys = new HashSet<string>();

                var matches2026 = Regex.Matches(xml2026, @"<file\s+name=""(2026/[^""]+\.(?:zip|rar))""");
                foreach (Match m in matches2026)
                {
                    string name = m.Groups[1].Value;
                    var (y, mo, d) = ParseDateFromVersion(name);
                    string extra = GetVersionExtra(name);
                    string key = y > 0 ? $"{y}_{mo:D2}_{d:D2}_{extra}" : name;

                    if (seenKeys.Add(key))
                    {
                        list.Add(name);
                    }
                }

                var matchesMain = Regex.Matches(xmlMain, @"<file\s+name=""(Build archive/[^""]+\.(?:zip|rar))""");
                foreach (Match m in matchesMain)
                {
                    string name = m.Groups[1].Value;
                    if (name.Contains(".rz.", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var (y, mo, d) = ParseDateFromVersion(name);
                    string extra = GetVersionExtra(name);
                    string key = y > 0 ? $"{y}_{mo:D2}_{d:D2}_{extra}" : name;

                    if (seenKeys.Add(key))
                    {
                        list.Add(name);
                    }
                }

                list.Sort((a, b) =>
                {
                    var (ya, ma, da) = ParseDateFromVersion(a);
                    var (yb, mb, db) = ParseDateFromVersion(b);

                    if (ya > 0 && yb > 0)
                    {
                        if (yb != ya) return yb.CompareTo(ya);
                        if (mb != ma) return mb.CompareTo(ma);
                        if (db != da) return db.CompareTo(da);
                    }
                    else if (ya > 0) return -1;
                    else if (yb > 0) return 1;

                    return string.Compare(b, a, StringComparison.Ordinal);
                });

                versions = list;

                if (versions.Count > 0)
                {
                    try
                    {
                        await File.WriteAllLinesAsync(cacheFile, versions);
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Warning("Catalog", "Could not persist the version catalog cache.", ex);
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warning("Catalog", "Could not download the version catalog; attempting stale cache fallback.", ex);
            }

            if (versions.Count == 0 && File.Exists(cacheFile))
            {
                try
                {
                    var lines = await File.ReadAllLinesAsync(cacheFile);
                    versions = lines.Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
                }
                catch (Exception ex)
                {
                    AppLogger.Warning("Catalog", "Could not read the stale version catalog cache.", ex);
                }
            }

            return versions;
        }

        public bool MigrateOldInstall(string installPath)
        {
            try
            {
                string newDir = OperatingSystem.IsWindows()
                    ? Path.Combine(installPath, "Latest")
                    : Path.Combine(installPath, "vers", "Latest");

                string[] candidateOldDirs = new[]
                {
                    Path.Combine(installPath, GameFolderName),
                    Path.Combine(installPath, "Yandere Simulator"),
                    Path.Combine(installPath, "Yandere_Simulator"),
                    Path.Combine(installPath, "Yandere-Simulator")
                };

                foreach (var oldDir in candidateOldDirs)
                {
                    if (Directory.Exists(oldDir) && !Directory.Exists(newDir) && FindDirectExecutable(oldDir) != null)
                    {
                        if (!OperatingSystem.IsWindows())
                            Directory.CreateDirectory(Path.Combine(installPath, "vers"));
                        Directory.Move(oldDir, newDir);
                        AppLogger.Log("Migration", $"Migrated legacy install directory from '{oldDir}' to '{newDir}'.");
                        break;
                    }
                }

                if (!OperatingSystem.IsWindows())
                {
                    string oldRunner = Path.Combine(installPath, "vers", "runner");
                    string newRunner = Path.Combine(installPath, "runner");
                    if (Directory.Exists(oldRunner) && !Directory.Exists(newRunner))
                    {
                        try
                        {
                            Directory.Move(oldRunner, newRunner);
                            AppLogger.Log("Migration", $"Migrated runner directory from '{oldRunner}' to '{newRunner}'.");
                        }
                        catch (Exception rex)
                        {
                            AppLogger.Warning("Migration", $"Could not move runner directory to '{newRunner}'.", rex);
                        }
                    }
                }

                string latestDir = OperatingSystem.IsWindows()
                    ? Path.Combine(installPath, "Latest")
                    : Path.Combine(installPath, "vers", "Latest");
                if (Directory.Exists(latestDir))
                {
                    string oldBytesFile = Path.Combine(latestDir, "ys_version_bytes.txt");
                    if (File.Exists(oldBytesFile))
                    {
                        if (long.TryParse(File.ReadAllText(oldBytesFile).Trim(), out long b))
                        {
                            SaveVersionBytes(installPath, "Latest", b, latestDir);
                        }
                        try { File.Delete(oldBytesFile); } catch { }
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Error("Migration", $"Could not migrate the legacy installation at '{installPath}'.", ex);
                return false;
            }
        }

        public static long? ReadSavedVersionBytes(string installPath, string version, string? gameDir = null)
        {
            try
            {
                // Check installPath/.vers.log
                string rootLog = Path.Combine(installPath, ".vers.log");
                if (File.Exists(rootLog))
                {
                    foreach (var line in File.ReadAllLines(rootLog))
                    {
                        var trimmed = line.Trim();
                        if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("#")) continue;
                        var parts = trimmed.Split(new[] { '=', ':' }, 2);
                        if (parts.Length == 2)
                        {
                            if (string.Equals(parts[0].Trim(), version, StringComparison.OrdinalIgnoreCase) &&
                                long.TryParse(parts[1].Trim(), out long b))
                            {
                                return b;
                            }
                        }
                        else if (parts.Length == 1 && version == "Latest" && long.TryParse(parts[0].Trim(), out long singleBytes))
                        {
                            return singleBytes;
                        }
                    }
                }

                // Check gameDir/.vers.log
                if (!string.IsNullOrEmpty(gameDir) && Directory.Exists(gameDir))
                {
                    string gameLog = Path.Combine(gameDir, ".vers.log");
                    if (File.Exists(gameLog))
                    {
                        string text = File.ReadAllText(gameLog).Trim();
                        if (long.TryParse(text, out long gb)) return gb;
                    }

                    // Backward compatibility: check ys_version_bytes.txt
                    string legacyBytesFile = Path.Combine(gameDir, "ys_version_bytes.txt");
                    if (File.Exists(legacyBytesFile))
                    {
                        string text = File.ReadAllText(legacyBytesFile).Trim();
                        if (long.TryParse(text, out long lb))
                        {
                            SaveVersionBytes(installPath, version, lb, gameDir);
                            try { File.Delete(legacyBytesFile); } catch { }
                            return lb;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warning("VersionBytes", "Could not read version bytes.", ex);
            }

            return null;
        }

        public static void SaveVersionBytes(string installPath, string version, long bytes, string? gameDir = null)
        {
            try
            {
                // Update installPath/.vers.log
                string rootLog = Path.Combine(installPath, ".vers.log");
                var entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (File.Exists(rootLog))
                {
                    foreach (var line in File.ReadAllLines(rootLog))
                    {
                        var trimmed = line.Trim();
                        if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("#")) continue;
                        var parts = trimmed.Split(new[] { '=', ':' }, 2);
                        if (parts.Length == 2)
                        {
                            entries[parts[0].Trim()] = parts[1].Trim();
                        }
                    }
                }

                entries[version] = bytes.ToString();

                var lines = entries.Select(kv => $"{kv.Key}={kv.Value}");
                File.WriteAllLines(rootLog, lines);

                if (OperatingSystem.IsWindows())
                {
                    try { File.SetAttributes(rootLog, File.GetAttributes(rootLog) | FileAttributes.Hidden); } catch { }
                }

                // Also write gameDir/.vers.log if gameDir exists
                if (!string.IsNullOrEmpty(gameDir) && Directory.Exists(gameDir))
                {
                    string gameLog = Path.Combine(gameDir, ".vers.log");
                    File.WriteAllText(gameLog, bytes.ToString());
                    if (OperatingSystem.IsWindows())
                    {
                        try { File.SetAttributes(gameLog, File.GetAttributes(gameLog) | FileAttributes.Hidden); } catch { }
                    }

                    string legacy = Path.Combine(gameDir, "ys_version_bytes.txt");
                    if (File.Exists(legacy))
                    {
                        try { File.Delete(legacy); } catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warning("VersionBytes", "Could not save version bytes.", ex);
            }
        }

        public async Task<UpdateCheckResult> CheckForUpdatesAsync(string installPath, CancellationToken cancellationToken = default)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                CachedDirectUrl = null;
                CachedRemoteSizeBytes = -1;
                LastError = null;

                string? mediaFirePageUrl = null;

                try
                {
                    string blogContent = await GetStringWithTimeoutAsync("https://yanderedev.wordpress.com/downloads/", TimeSpan.FromSeconds(15), cancellationToken);
                    var mfBlogMatch = Regex.Match(blogContent, @"https?://(?:www\.)?mediafire\.com/file/[a-zA-Z0-9_]+/[^\s""'<>]+");
                    if (mfBlogMatch.Success)
                    {
                        mediaFirePageUrl = mfBlogMatch.Value;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    AppLogger.Warning("UpdateCheck", "Could not query the WordPress download page.", ex);
                }

                if (string.IsNullOrEmpty(mediaFirePageUrl))
                {
                    try
                    {
                        string wpContent = await GetStringWithTimeoutAsync(WP_URL, TimeSpan.FromSeconds(15), cancellationToken);
                        var mfMatch = Regex.Match(wpContent, @"https?://(?:www\.)?mediafire\.com/file/[a-zA-Z0-9_]+/[^\s""'<>]+");
                        if (mfMatch.Success)
                        {
                            mediaFirePageUrl = mfMatch.Value;
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Warning("UpdateCheck", "Could not query the official download page.", ex);
                    }
                }

                DateTime? remoteDate = null;

                if (!string.IsNullOrEmpty(mediaFirePageUrl))
                {
                    try
                    {
                        var qkMatch = Regex.Match(mediaFirePageUrl, @"mediafire\.com/file/([a-zA-Z0-9]+)");
                        if (qkMatch.Success)
                        {
                            string quickKey = qkMatch.Groups[1].Value;
                            string apiUrl = $"https://www.mediafire.com/api/1.4/file/get_info.php?quick_key={quickKey}&response_format=json";
                            try
                            {
                                string apiJson = await GetStringWithTimeoutAsync(apiUrl, TimeSpan.FromSeconds(10), cancellationToken);
                                using var doc = JsonDocument.Parse(apiJson);
                                if (doc.RootElement.TryGetProperty("response", out var resp) &&
                                    resp.TryGetProperty("file_info", out var fileInfo))
                                {
                                    if (fileInfo.TryGetProperty("created", out var createdProp) &&
                                        createdProp.GetString() is string cStr &&
                                        DateTime.TryParse(cStr, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime pDate))
                                    {
                                        remoteDate = pDate.Date;
                                    }
                                    else if (fileInfo.TryGetProperty("created_utc", out var createdUtcProp) &&
                                        createdUtcProp.GetString() is string utcStr &&
                                        DateTime.TryParse(utcStr, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out DateTime pUtc))
                                    {
                                        try
                                        {
                                            var tz = TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");
                                            remoteDate = TimeZoneInfo.ConvertTimeFromUtc(pUtc, tz).Date;
                                        }
                                        catch
                                        {
                                            remoteDate = pUtc.AddHours(-7).Date;
                                        }
                                    }

                                    if (fileInfo.TryGetProperty("size", out var sizeProp) &&
                                        sizeProp.GetString() is string sizeStr &&
                                        long.TryParse(sizeStr, out long pSize))
                                    {
                                        CachedRemoteSizeBytes = pSize;
                                    }

                                    if (fileInfo.TryGetProperty("links", out var linksProp))
                                    {
                                        if (linksProp.TryGetProperty("direct_download", out var ddProp) && !string.IsNullOrEmpty(ddProp.GetString()))
                                        {
                                            CachedDirectUrl = ddProp.GetString();
                                        }
                                    }
                                }
                            }
                            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                            {
                                throw;
                            }
                            catch (Exception ex)
                            {
                                AppLogger.Warning("UpdateCheck", "Could not query MediaFire API for file info.", ex);
                            }
                        }

                        // Always attempt to resolve MediaFire direct download URL if not yet acquired
                        if (string.IsNullOrEmpty(CachedDirectUrl) || remoteDate == null)
                        {
                            try
                            {
                                string mfContent = await GetStringWithTimeoutAsync(mediaFirePageUrl, TimeSpan.FromSeconds(10), cancellationToken);
                                var mfDirectMatch = Regex.Match(mfContent, @"https?://download\d*\.mediafire\.com/[^\s""']+");
                                if (mfDirectMatch.Success && string.IsNullOrEmpty(CachedDirectUrl))
                                {
                                    CachedDirectUrl = mfDirectMatch.Value;
                                }

                                if (remoteDate == null)
                                {
                                    var dateMatch = Regex.Match(mfContent, @"Uploaded:\s*<span>(\d{4})-(\d{2})-(\d{2})", RegexOptions.IgnoreCase);
                                    if (!dateMatch.Success) dateMatch = Regex.Match(mfContent, @"(\d{4})-(\d{2})-(\d{2})");
                                    if (dateMatch.Success && int.TryParse(dateMatch.Groups[1].Value, out int ry) &&
                                        int.TryParse(dateMatch.Groups[2].Value, out int rm) &&
                                        int.TryParse(dateMatch.Groups[3].Value, out int rd))
                                    {
                                        remoteDate = new DateTime(ry, rm, rd);
                                    }
                                }
                            }
                            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                            {
                                throw;
                            }
                            catch (HttpRequestException httpEx) when (httpEx.StatusCode == System.Net.HttpStatusCode.Forbidden)
                            {
                                AppLogger.Log("UpdateCheck", "MediaFire download page requires browser challenge; falling back to alternative metadata sources.");
                            }
                            catch (Exception ex)
                            {
                                AppLogger.Warning("UpdateCheck", "Could not inspect the MediaFire download page.", ex);
                            }
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Warning("UpdateCheck", "MediaFire metadata inspection encountered an error.", ex);
                    }
                }

                // Resolve official direct zip URL proactively so download is immediately available
                if (string.IsNullOrEmpty(CachedDirectUrl))
                {
                    try
                    {
                        string wpContent = await GetStringWithTimeoutAsync(WP_URL, TimeSpan.FromSeconds(15), cancellationToken);
                        var directZipMatch = Regex.Match(wpContent, @"href=""(https://yanderesimulator\.com/dl/[^""]+)""");
                        if (directZipMatch.Success)
                        {
                            CachedDirectUrl = directZipMatch.Groups[1].Value.Replace("&amp;", "&");
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Warning("UpdateCheck", "Could not resolve the direct download URL from official site.", ex);
                    }
                }

                if (!string.IsNullOrEmpty(CachedDirectUrl))
                {
                    try
                    {
                        using var response = await GetAsyncWithTimeoutAsync(CachedDirectUrl, HttpCompletionOption.ResponseHeadersRead, TimeSpan.FromSeconds(15), cancellationToken);
                        if (response.IsSuccessStatusCode)
                        {
                            if (response.Content.Headers.ContentLength.HasValue && CachedRemoteSizeBytes <= 0)
                            {
                                CachedRemoteSizeBytes = response.Content.Headers.ContentLength.Value;
                            }
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Warning("UpdateCheck", "Could not read the remote archive headers.", ex);
                    }
                }

                if (remoteDate == null)
                {
                    try
                    {
                        string xml2026 = await GetStringWithTimeoutAsync("https://archive.org/download/yandere-simulator-2026-archive/yandere-simulator-2026-archive_files.xml", TimeSpan.FromSeconds(15), cancellationToken);
                        var matches = Regex.Matches(xml2026, @"<file\s+name=""(2026/[^""]+\.(?:zip|rar))""");
                        DateTime? maxArchDate = null;
                        foreach (Match m in matches)
                        {
                            var (y, mo, d) = ParseDateFromVersion(m.Groups[1].Value);
                            if (y > 0 && mo > 0 && d > 0)
                            {
                                var dt = new DateTime(y, mo, d);
                                if (maxArchDate == null || dt > maxArchDate) maxArchDate = dt;
                            }
                        }
                        if (maxArchDate != null) remoteDate = maxArchDate;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Warning("UpdateCheck", "Could not query the current Archive.org catalog.", ex);
                    }
                }

                string? gameDir = GetGameDirectory(installPath, "Latest");
                DateTime? localBuildDate = null;

                if (!string.IsNullOrEmpty(gameDir) && FindExecutable(gameDir) != null)
                {
                    localBuildDate = GetLocalBuildDate(installPath, "Latest");
                }
                else
                {
                    return UpdateCheckResult.NotInstalled;
                }

                if (localBuildDate != null && remoteDate != null)
                {
                    return localBuildDate.Value.Date < remoteDate.Value.Date
                        ? UpdateCheckResult.UpdateAvailable
                        : UpdateCheckResult.UpToDate;
                }
                if (localBuildDate != null && localBuildDate.Value.Year < DateTime.Now.Year)
                {
                    return UpdateCheckResult.UpdateAvailable;
                }

                if (localBuildDate == null)
                {
                    return UpdateCheckResult.NotSupported;
                }

                if (CachedRemoteSizeBytes != -1)
                {
                    long? localSize = ReadSavedVersionBytes(installPath, "Latest", gameDir);
                    if (localSize != null)
                    {
                        return localSize.Value != CachedRemoteSizeBytes
                            ? UpdateCheckResult.UpdateAvailable
                            : UpdateCheckResult.UpToDate;
                    }
                }

                LastError = I18n.T("UpdateCheckFailed");
                return UpdateCheckResult.Failed;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                LastError = I18n.T("UpdateCheckFailed");
                AppLogger.Warning("UpdateCheck", "Unexpected update check failure.", ex);
                return UpdateCheckResult.Failed;
            }
        }

        public async Task<bool> CheckAndDownloadUpdateAsync(string installPath, string version, CancellationToken cancellationToken = default)
        {
            LastError = null;
            string? archivePath = null;
            string? stagingDir = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                string urlToDownload = "";
                string cleanFolderName = Path.GetFileNameWithoutExtension(version).Replace(".rz", "");
                string archiveName = Path.GetFileName(version);

                if (version == "Latest")
                {
                    if (string.IsNullOrEmpty(CachedDirectUrl))
                    {
                        StatusChanged?.Invoke(this, I18n.T("PrepDL"));
                        await CheckForUpdatesAsync(installPath, cancellationToken);
                    }

                    if (string.IsNullOrEmpty(CachedDirectUrl))
                    {
                        LastError = I18n.T("ErrLink");
                        StatusChanged?.Invoke(this, LastError);
                        return false;
                    }
                    urlToDownload = CachedDirectUrl;
                    archiveName = "YandereSim_Latest.zip";
                }
                else
                {
                    string safePath = Uri.EscapeDataString(version).Replace("%2F", "/");
                    if (version.StartsWith("2026/"))
                    {
                        urlToDownload = $"https://archive.org/download/yandere-simulator-2026-archive/{safePath}";
                    }
                    else
                    {
                        urlToDownload = $"https://archive.org/download/yandere-simulator-builds/{safePath}";
                    }
                    StatusChanged?.Invoke(this, I18n.T("ConnectingArchive"));
                }

                archivePath = Path.Combine(installPath, archiveName);
                string finalGameDir = OperatingSystem.IsWindows()
                    ? Path.Combine(installPath, cleanFolderName)
                    : Path.Combine(installPath, "vers", cleanFolderName);
                stagingDir = finalGameDir + $".installing-{Guid.NewGuid():N}";
                string gameDir = stagingDir;

                Directory.CreateDirectory(gameDir);

                StatusChanged?.Invoke(this, I18n.T("Downloading"));
                await DownloadFileMultiThreadedAsync(urlToDownload, archivePath, 4, cancellationToken: cancellationToken);

                StatusChanged?.Invoke(this, string.Format(I18n.T("ExtractingProgress"), 0));
                ProgressChanged?.Invoke(this, 0);
                
                bool extracted = false;
                try
                {
                    if (archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        using (var archive = ZipFile.OpenRead(archivePath))
                        {
                            if (!archive.Entries.Any(entry => entry.Name == ExecutableName))
                                throw new FileNotFoundException(I18n.T("MissingExe"));
                            StorageSpace.EnsureAvailable(gameDir, archive.Entries.Sum(entry => entry.Length));
                            int totalEntries = archive.Entries.Count;
                            int entryIndex = 0;
                            int lastPercent = -1;
                            long lastExtractReport = 0;

                            foreach (var entry in archive.Entries)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                if (string.IsNullOrEmpty(entry.Name))
                                {
                                    entryIndex++;
                                    continue;
                                }

                                string fixedFullName = entry.FullName.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
                                string destFileName = GetSafeExtractionPath(gameDir, fixedFullName);

                                string? dirName = Path.GetDirectoryName(destFileName);
                                if (!string.IsNullOrEmpty(dirName)) Directory.CreateDirectory(dirName);

                                if (File.Exists(destFileName)) File.SetAttributes(destFileName, FileAttributes.Normal);

                                using (var entryStream = entry.Open())
                                await using (var fileStream = new FileStream(destFileName, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                                {
                                    await entryStream.CopyToAsync(fileStream, cancellationToken);
                                }
                                entryIndex++;

                                int percent = totalEntries > 0 ? (entryIndex * 100 / totalEntries) : 0;
                                long now = Environment.TickCount64;
                                if ((percent != lastPercent && now - lastExtractReport > 200) || percent == 100)
                                {
                                    lastPercent = percent;
                                    lastExtractReport = now;
                                    StatusChanged?.Invoke(this, string.Format(I18n.T("ExtractingProgress"), percent));
                                    ProgressChanged?.Invoke(this, percent);
                                }
                            }
                        }
                    }
                    else if (archivePath.EndsWith(".rar", StringComparison.OrdinalIgnoreCase))
                    {
                        using (var archive = ArchiveFactory.OpenArchive(archivePath))
                        {
                            var entriesList = archive.Entries.Where(e => !e.IsDirectory && !string.IsNullOrEmpty(e.Key)).ToList();
                            if (!entriesList.Any(entry => entry.Key!.Replace('\\', '/').Split('/').Last() == ExecutableName))
                                throw new FileNotFoundException(I18n.T("MissingExe"));
                            StorageSpace.EnsureAvailable(gameDir, entriesList.Sum(entry => entry.Size));
                            int totalEntries = entriesList.Count;
                            int entryIndex = 0;
                            int lastPercent = -1;
                            long lastExtractReport = 0;

                            foreach (var entry in entriesList)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                string fixedFullName = entry.Key!.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
                                string destFileName = GetSafeExtractionPath(gameDir, fixedFullName);

                                string? dirName = Path.GetDirectoryName(destFileName);
                                if (!string.IsNullOrEmpty(dirName)) Directory.CreateDirectory(dirName);

                                if (File.Exists(destFileName)) File.SetAttributes(destFileName, FileAttributes.Normal);

                                using (var entryStream = entry.OpenEntryStream())
                                await using (var fs = new FileStream(destFileName, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                                {
                                    await entryStream.CopyToAsync(fs, cancellationToken);
                                }
                                entryIndex++;

                                int percent = totalEntries > 0 ? (entryIndex * 100 / totalEntries) : 0;
                                long now = Environment.TickCount64;
                                if ((percent != lastPercent && now - lastExtractReport > 200) || percent == 100)
                                {
                                    lastPercent = percent;
                                    lastExtractReport = now;
                                    StatusChanged?.Invoke(this, string.Format(I18n.T("ExtractingProgress"), percent));
                                    ProgressChanged?.Invoke(this, percent);
                                }
                            }
                        }
                    }
                    else throw new InvalidDataException("Unsupported game archive format.");
                    extracted = true;
                }
                finally
                {
                    TryDeleteFile(archivePath, "InstallCleanup");
                }

                if (extracted)
                {
                    try
                    {
                        string nestedDir = Path.Combine(gameDir, GameFolderName);
                        if (Directory.Exists(nestedDir) && File.Exists(Path.Combine(nestedDir, ExecutableName)))
                        {
                            foreach (var file in Directory.GetFiles(nestedDir))
                            {
                                string dest = Path.Combine(gameDir, Path.GetFileName(file));
                                if (File.Exists(dest)) File.Delete(dest);
                                File.Move(file, dest);
                            }
                            foreach (var sub in Directory.GetDirectories(nestedDir))
                            {
                                string dest = Path.Combine(gameDir, Path.GetFileName(sub));
                                if (Directory.Exists(dest)) Directory.Delete(dest, true);
                                Directory.Move(sub, dest);
                            }
                            try
                            {
                                Directory.Delete(nestedDir, true);
                            }
                            catch (Exception ex)
                            {
                                AppLogger.Warning("InstallCleanup", $"Could not remove empty nested directory '{nestedDir}'.", ex);
                            }
                        }
                    }
                    catch (Exception ex) { throw new IOException("Could not finalize the game installation.", ex); }

                    if (FindExecutable(gameDir) == null) throw new FileNotFoundException(I18n.T("MissingExe"));
                    if (version == "Latest" && CachedRemoteSizeBytes > 0)
                    {
                        SaveVersionBytes(installPath, version, CachedRemoteSizeBytes, gameDir);
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    CommitStagedDirectory(gameDir, finalGameDir);
                    if (version == "Latest" && CachedRemoteSizeBytes > 0)
                    {
                        SaveVersionBytes(installPath, version, CachedRemoteSizeBytes, finalGameDir);
                    }
                    stagingDir = null;
                    _buildDateCache.Clear();
                    _installedMapCache.Clear();

                    LastError = null;
                    StatusChanged?.Invoke(this, I18n.T("Success"));
                    DownloadCompleted?.Invoke(this, EventArgs.Empty);
                    return true;
                }
            }
            catch (OperationCanceledException)
            {
                LastError = null;
                StatusChanged?.Invoke(this, I18n.T("DownloadCancelled"));
                throw;
            }
            catch (Exception ex)
            {
                LastError = $"Error: {ex.Message}";
                AppLogger.Error("Install", $"Could not install version '{version}' into '{installPath}'.", ex);
                StatusChanged?.Invoke(this, LastError);
            }
            finally
            {
                if (!string.IsNullOrEmpty(archivePath)) TryDeleteFile(archivePath, "InstallCleanup");
                if (!string.IsNullOrEmpty(stagingDir)) TryDeleteDirectory(stagingDir, "InstallCleanup");
            }
            return false;
        }

        public static string FormatBytes(long bytes)
        {
            if (bytes <= 0) return "0 MB";
            double mb = bytes / (1024.0 * 1024.0);
            if (mb >= 1024.0)
            {
                double gb = mb / 1024.0;
                return $"{gb:F2} GB";
            }
            return $"{mb:F1} MB";
        }

        public async Task DownloadFileMultiThreadedAsync(string url, string destination, int chunks, string? prefix = null, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(url);
            ArgumentException.ThrowIfNullOrWhiteSpace(destination);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(chunks);
            cancellationToken.ThrowIfCancellationRequested();

            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            long totalBytes = response.Content.Headers.ContentLength ?? -1L;
            StorageSpace.EnsureAvailable(destination, StorageSpace.DownloadBudget(totalBytes, 2 * StorageSpace.GiB));
            
            bool serverSupportsRanges = response.Headers.AcceptRanges != null && response.Headers.AcceptRanges.Contains("bytes");
            if (totalBytes <= 0 || !serverSupportsRanges)
            {
                await DownloadSingleStreamAsync(response, destination, totalBytes, prefix, cancellationToken);
                return;
            }

            chunks = (int)Math.Min(chunks, totalBytes);
            long chunkSize = totalBytes / chunks;
            var tasks = new List<Task>();
            long totalRead = 0;
            int lastPercentage = -1;
            long lastReportTime = 0;
            object progressLock = new();
            string tempDir = destination + ".tmp_" + Environment.TickCount64;
            Directory.CreateDirectory(tempDir);
            string[] tempFiles = new string[chunks];
            for (int i = 0; i < chunks; i++) tempFiles[i] = Path.Combine(tempDir, $"part_{i}.bin");

            int fallbackToSingle = 0;

            try
            {
                for (int i = 0; i < chunks; i++)
                {
                    int chunkIndex = i;
                    long start = chunkIndex * chunkSize;
                    long end = (chunkIndex == chunks - 1) ? totalBytes - 1 : start + chunkSize - 1;

                    tasks.Add(Task.Run(async () =>
                    {
                        using var request = new HttpRequestMessage(HttpMethod.Get, url);
                        request.Headers.Range = new RangeHeaderValue(start, end);

                        using var chunkResponse = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                        
                        var contentRange = chunkResponse.Content.Headers.ContentRange;
                        if (chunkResponse.StatusCode != System.Net.HttpStatusCode.PartialContent ||
                            contentRange?.From != start || contentRange.To != end)
                        {
                            Interlocked.Exchange(ref fallbackToSingle, 1);
                            return;
                        }

                        using var stream = await chunkResponse.Content.ReadAsStreamAsync(cancellationToken);
                        await using (var fileStream = new FileStream(tempFiles[chunkIndex], FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
                        {
                            var buffer = new byte[8192];
                            int read;
                            long chunkRead = 0;
                            while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) != 0)
                            {
                                if (Volatile.Read(ref fallbackToSingle) != 0) return;
                                await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);

                                chunkRead += read;
                                long downloaded = Interlocked.Add(ref totalRead, read);
                                int percentage = (int)((downloaded * 100) / totalBytes);

                                lock (progressLock)
                                {
                                    long now = Environment.TickCount64;
                                    if ((percentage != lastPercentage && now - lastReportTime > 200) || percentage == 100)
                                    {
                                        lastPercentage = percentage;
                                        lastReportTime = now;
                                        ProgressChanged?.Invoke(this, percentage);
                                        string curStr = FormatBytes(downloaded);
                                        string totStr = FormatBytes(totalBytes);
                                        string text = !string.IsNullOrEmpty(prefix)
                                            ? $"{prefix}: {curStr} / {totStr} ({percentage}%)"
                                            : string.Format(I18n.T("GameDLProgress"), curStr, totStr, percentage);
                                        StatusChanged?.Invoke(this, text);
                                    }
                                }
                            }

                            if (chunkRead != end - start + 1)
                                throw new IOException("The server returned an incomplete download chunk.");

                            await fileStream.FlushAsync(cancellationToken);
                        }
                    }, cancellationToken));
                }

                await Task.WhenAll(tasks);

                if (Volatile.Read(ref fallbackToSingle) != 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using var singleResp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                    await DownloadSingleStreamAsync(singleResp, destination, totalBytes, prefix, cancellationToken);
                    return;
                }

                StatusChanged?.Invoke(this, I18n.T("FinalizingDL"));
                ProgressChanged?.Invoke(this, 100);

                await using (var finalStream = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                {
                    for (int i = 0; i < chunks; i++)
                    {
                        if (File.Exists(tempFiles[i]))
                        {
                            await using (var partStream = new FileStream(tempFiles[i], FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true))
                            {
                                await partStream.CopyToAsync(finalStream, cancellationToken);
                            }
                        }
                    }

                    await finalStream.FlushAsync(cancellationToken);

                    if (finalStream.Length != totalBytes)
                        throw new IOException("The assembled download size does not match the server response.");
                }
            }
            finally
            {
                // Ensure all chunk download tasks complete before cleaning up temporary directory
                try { await Task.WhenAll(tasks); } catch { }

                try
                {
                    if (Directory.Exists(tempDir))
                    {
                        Directory.Delete(tempDir, recursive: true);
                    }
                }
                catch
                {
                    // Ignora se o SO demorar alguns ms para liberar a pasta; nao quebra o fluxo
                }
            }
        }

        private async Task DownloadSingleStreamAsync(HttpResponseMessage response, string destination, long totalBytes, string? prefix, CancellationToken cancellationToken)
        {
            response.EnsureSuccessStatusCode();
            var canReportProgress = totalBytes > 0;
            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var fileStream = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

            var buffer = new byte[8192];
            long totalRead = 0;
            int read;
            int lastPercentage = -1;
            long lastReportTime = 0;

            while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) != 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                totalRead += read;

                if (canReportProgress)
                {
                    int percentage = (int)((totalRead * 100) / totalBytes);
                    long now = Environment.TickCount64;
                    if ((percentage != lastPercentage && now - lastReportTime > 200) || percentage == 100)
                    {
                        lastPercentage = percentage;
                        lastReportTime = now;
                        ProgressChanged?.Invoke(this, percentage);
                        string curStr = FormatBytes(totalRead);
                        string totStr = FormatBytes(totalBytes);
                        string text = !string.IsNullOrEmpty(prefix)
                            ? $"{prefix}: {curStr} / {totStr} ({percentage}%)"
                            : string.Format(I18n.T("GameDLProgress"), curStr, totStr, percentage);
                        StatusChanged?.Invoke(this, text);
                    }
                }
            }

            if (totalBytes > 0 && totalRead != totalBytes)
                throw new IOException("The downloaded file size does not match the server response.");
        }

        private static string GetSafeExtractionPath(string destinationRoot, string entryPath)
        {
            string root = Path.GetFullPath(destinationRoot);
            string rootPrefix = Path.EndsInDirectorySeparator(root)
                ? root
                : root + Path.DirectorySeparatorChar;
            string destination = Path.GetFullPath(Path.Combine(root, entryPath));
            StringComparison comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            if (!destination.StartsWith(rootPrefix, comparison))
                throw new InvalidDataException("The archive contains an invalid file path.");

            return destination;
        }

        public string? FindDirectExecutable(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return null;

            try
            {
                string directExe = Path.Combine(path, ExecutableName);
                if (File.Exists(directExe)) return directExe;

                foreach (var exeFile in Directory.GetFiles(path, "*.exe"))
                {
                    string fn = Path.GetFileName(exeFile);
                    if (fn.Contains("Yandere", StringComparison.OrdinalIgnoreCase) &&
                        !fn.Contains("CrashHandler", StringComparison.OrdinalIgnoreCase) &&
                        !fn.Contains("unins", StringComparison.OrdinalIgnoreCase))
                    {
                        return exeFile;
                    }
                }
            }
            catch { }

            return null;
        }

        public string? FindExecutable(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return null;

            try
            {
                string? direct = FindDirectExecutable(path);
                if (direct != null) return direct;

                foreach (var subDir in Directory.GetDirectories(path))
                {
                    string dirName = Path.GetFileName(subDir);
                    if (dirName.Equals("prefix", StringComparison.OrdinalIgnoreCase) ||
                        dirName.Equals("dosdevices", StringComparison.OrdinalIgnoreCase) ||
                        dirName.Equals("runner", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string? subExe = FindDirectExecutable(subDir);
                    if (subExe != null) return subExe;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warning("Discovery", $"Could not search for the executable under '{path}'.", ex);
            }

            return null;
        }

        public static string? FindPoseModExecutable(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return null;

            try
            {
                string[] candidates = { "PoseMod64.exe", "Posemod64.exe", "PoseMod.exe", "Posemod.exe", "PoseMod32.exe", "Posemod32.exe" };
                foreach (var candidate in candidates)
                {
                    string direct = Path.Combine(path, candidate);
                    if (File.Exists(direct)) return direct;
                }

                foreach (var subDir in Directory.GetDirectories(path))
                {
                    string dirName = Path.GetFileName(subDir);
                    if (dirName.Equals("prefix", StringComparison.OrdinalIgnoreCase) ||
                        dirName.Equals("dosdevices", StringComparison.OrdinalIgnoreCase) ||
                        dirName.Equals("runner", StringComparison.OrdinalIgnoreCase) ||
                        dirName.StartsWith(".backup-", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    foreach (var candidate in candidates)
                    {
                        string subExe = Path.Combine(subDir, candidate);
                        if (File.Exists(subExe)) return subExe;
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warning("PoseMod", $"Could not search for PoseMod executable under '{path}'.", ex);
            }

            return null;
        }

        public static bool HasPoseMod(string? path)
        {
            return FindPoseModExecutable(path) != null;
        }

        public static bool HasPoseModFiles(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return false;
            if (FindPoseModExecutable(path) != null) return true;
            try
            {
                return Directory.EnumerateDirectories(path).Any(d =>
                    string.Equals(Path.GetFileName(d), "Posemod", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(Path.GetFileName(d), "PoseMod", StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                return false;
            }
        }

        public record GameInstallationInfo(bool IsInstalled, string? GameDirectory, string? ExecutablePath, DateTime? BuildDate);

        public GameInstallationInfo GetInstallationInfo(string installPath, string version, Dictionary<string, string>? map = null)
        {
            if (string.IsNullOrWhiteSpace(installPath))
                return new GameInstallationInfo(false, null, null, null);

            map ??= GetInstalledVersionsMap(installPath);
            bool isInstalled = (version == "Latest")
                ? map.ContainsKey("Latest")
                : MatchesInstalledMap(version, map);

            string? gameDir = isInstalled ? GetGameDirectory(installPath, version, map) : null;
            string? gameExe = isInstalled && !string.IsNullOrEmpty(gameDir) ? FindExecutable(gameDir) : null;
            DateTime? buildDate = isInstalled && !string.IsNullOrEmpty(gameDir) ? GetLocalBuildDateFromDirectory(gameDir) : null;

            return new GameInstallationInfo(isInstalled, gameDir, gameExe, buildDate);
        }

        public string? GetGameDirectory(string installPath, string version, Dictionary<string, string>? map = null)
        {
            if (string.IsNullOrWhiteSpace(installPath)) return null;
            map ??= GetInstalledVersionsMap(installPath);

            if (version == "Latest")
            {
                if (map.TryGetValue("Latest", out var latestDir)) return latestDir;
                return OperatingSystem.IsWindows()
                    ? Path.Combine(installPath, "Latest")
                    : Path.Combine(installPath, "vers", "Latest");
            }

            if (map.TryGetValue(version, out var directDir)) return directDir;

            string cleanFolderName = Path.GetFileNameWithoutExtension(version).Replace(".rz", "");
            if (map.TryGetValue(cleanFolderName, out var cleanDir)) return cleanDir;

            var (year, month, day) = ParseDateFromVersion(version);
            if (year > 0 && month > 0 && day > 0)
            {
                string tag1 = $"ys_{year}_{month:D2}_{day:D2}";
                if (map.TryGetValue(tag1, out var tag1Dir)) return tag1Dir;
                string tag2 = $"ys{year}-{month:D2}-{day:D2}";
                if (map.TryGetValue(tag2, out var tag2Dir)) return tag2Dir;
            }

            return OperatingSystem.IsWindows()
                ? Path.Combine(installPath, cleanFolderName)
                : Path.Combine(installPath, "vers", cleanFolderName);
        }

        public static string GetPrefixPath(string installPath, LinuxRunner? runner)
        {
            string basePrefixDir = Path.Combine(installPath, "vers", "prefix");
            if (runner == null) return basePrefixDir;

            string safeName = "default";
            string runnerName = runner.Name.ToLowerInvariant();
            if (runnerName.Contains("crossover") && (runnerName.Contains("24") || runnerName.Contains("wine 9")))
                safeName = "wine_crossover_24";
            else if ((runnerName.Contains("crossover") || runnerName.Contains("wc")) && (runnerName.Contains("23") || runnerName.Contains("wine 8")))
                safeName = "wine_crossover_23";
            else if (runnerName.Contains("gptk") || runnerName.Contains("game porting toolkit"))
                safeName = "gptk";
            else if (runner.IsProton || runnerName.Contains("proton"))
                safeName = "proton";
            else if (runnerName.Contains("staging"))
                safeName = "wine_staging";
            else
                safeName = Regex.Replace(runner.Name, @"[^a-zA-Z0-9_\-]", "_").ToLowerInvariant();

            string runnerPrefix = Path.Combine(basePrefixDir, safeName);
            Directory.CreateDirectory(runnerPrefix);
            return runnerPrefix;
        }

        public string NormalizeInstallPath(string selectedPath, out string? detectedVersion)
        {
            detectedVersion = null;
            if (string.IsNullOrWhiteSpace(selectedPath)) return selectedPath;

            try
            {
                string cleanPath = Path.GetFullPath(selectedPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (!Directory.Exists(cleanPath)) return cleanPath;

                string folderName = Path.GetFileName(cleanPath);
                var parent = Directory.GetParent(cleanPath);

                if (folderName.Equals("vers", StringComparison.OrdinalIgnoreCase) && parent != null)
                {
                    return parent.FullName;
                }

                if (parent != null && parent.Name.Equals("vers", StringComparison.OrdinalIgnoreCase))
                {
                    var root = parent.Parent;
                    if (root != null)
                    {
                        if (!folderName.Equals("prefix", StringComparison.OrdinalIgnoreCase) &&
                            !folderName.Equals("runner", StringComparison.OrdinalIgnoreCase) &&
                            !folderName.Equals("dosdevices", StringComparison.OrdinalIgnoreCase))
                        {
                            detectedVersion = folderName;
                        }
                        return root.FullName;
                    }
                }

                if (parent != null && parent.Parent != null && parent.Parent.Name.Equals("vers", StringComparison.OrdinalIgnoreCase))
                {
                    var root = parent.Parent.Parent;
                    if (root != null)
                    {
                        if (!parent.Name.Equals("prefix", StringComparison.OrdinalIgnoreCase) &&
                            !parent.Name.Equals("runner", StringComparison.OrdinalIgnoreCase) &&
                            !parent.Name.Equals("dosdevices", StringComparison.OrdinalIgnoreCase))
                        {
                            detectedVersion = parent.Name;
                        }
                        return root.FullName;
                    }
                }

                if (FindDirectExecutable(cleanPath) != null)
                {
                    var bDate = GetLocalBuildDateFromDirectory(cleanPath);
                    string verTag = (bDate != null && bDate.Value.Year < 2024)
                        ? $"ys_{bDate.Value.Year}_{bDate.Value.Month:D2}_{bDate.Value.Day:D2}"
                        : "Latest";

                    if (parent != null && (Directory.Exists(Path.Combine(parent.FullName, "vers")) || folderName.Equals(GameFolderName, StringComparison.OrdinalIgnoreCase)))
                    {
                        detectedVersion = verTag;
                        return parent.FullName;
                    }
                    else
                    {
                        detectedVersion = verTag;
                        return cleanPath;
                    }
                }

                return cleanPath;
            }
            catch (Exception ex)
            {
                AppLogger.Warning("Path", $"Could not normalize installation path '{selectedPath}'.", ex);
                return selectedPath;
            }
        }

        public string? FindVersionInCatalog(IEnumerable<string> catalogVersions, string folderOrVersionName, string installPath)
        {
            if (string.IsNullOrEmpty(folderOrVersionName)) return null;

            string cleanTarget = Path.GetFileNameWithoutExtension(folderOrVersionName).Replace(".rz", "").ToLowerInvariant();
            foreach (var v in catalogVersions)
            {
                string cleanV = Path.GetFileNameWithoutExtension(v).Replace(".rz", "").ToLowerInvariant();
                if (cleanV == cleanTarget)
                {
                    return v;
                }
            }

            var (year, month, day) = ParseDateFromVersion(folderOrVersionName);
            if (year > 0 && month > 0 && day > 0)
            {
                foreach (var v in catalogVersions)
                {
                    var (vy, vm, vd) = ParseDateFromVersion(v);
                    if (vy == year && vm == month && vd == day)
                    {
                        return v;
                    }
                }
            }

            try
            {
                string dirToCheck = Directory.Exists(folderOrVersionName)
                    ? folderOrVersionName
                    : (Directory.Exists(Path.Combine(installPath, "vers", folderOrVersionName))
                        ? Path.Combine(installPath, "vers", folderOrVersionName)
                        : (Directory.Exists(Path.Combine(installPath, folderOrVersionName))
                            ? Path.Combine(installPath, folderOrVersionName)
                            : installPath));

                var dt = GetLocalBuildDateFromDirectory(dirToCheck);
                if (dt.HasValue)
                {
                    foreach (var v in catalogVersions)
                    {
                        var (vy, vm, vd) = ParseDateFromVersion(v);
                        if (vy == dt.Value.Year && vm == dt.Value.Month && vd == dt.Value.Day)
                        {
                            return v;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warning("Versions", $"Could not match local version '{folderOrVersionName}' to the catalog.", ex);
            }

            return null;
        }

        public bool LaunchGame(string installPath, string version, LinuxRunner? linuxRunner = null, bool launchPoseMod = false)
        {
            return LaunchGameProcess(installPath, version, linuxRunner, launchPoseMod) != null;
        }

        public Process? LaunchGameProcess(string installPath, string version, LinuxRunner? linuxRunner = null, bool launchPoseMod = false)
        {
            string? gameDir = GetGameDirectory(installPath, version);
            if (string.IsNullOrEmpty(gameDir) || FindExecutable(gameDir) == null)
            {
                if (version == "Latest")
                {
                    if (FindExecutable(installPath) != null)
                    {
                        gameDir = installPath;
                    }
                }
            }
            if (string.IsNullOrEmpty(gameDir))
            {
                LastError = I18n.T("MissingExe");
                return null;
            }
            string? gameExe = launchPoseMod ? FindPoseModExecutable(gameDir) : FindExecutable(gameDir);
            if (launchPoseMod && gameExe == null)
            {
                AppLogger.Warning("Launch", $"PoseMod executable requested but not found in '{gameDir}'. Falling back to regular executable.");
                gameExe = FindExecutable(gameDir);
            }

            if (gameExe != null)
            {
                AppLogger.Log("Launch", $"Launching '{gameExe}' (PoseMod: {launchPoseMod})");
                bool isLinux = OperatingSystem.IsLinux();
                bool isMac = OperatingSystem.IsMacOS();
                bool isWindows = OperatingSystem.IsWindows();
                string workingDir = Path.GetDirectoryName(gameExe) ?? gameDir;

                var psi = new ProcessStartInfo
                {
                    FileName = (isLinux || isMac) ? (linuxRunner?.Path ?? "wine") : gameExe,
                    WorkingDirectory = workingDir,
                    UseShellExecute = isWindows
                };

                if (isLinux || isMac) psi.ArgumentList.Add(gameExe);

                if ((isLinux || isMac) && linuxRunner != null)
                {
                    string prefixPath = GetPrefixPath(installPath, linuxRunner);
                    
                    if (linuxRunner.IsProton)
                    {
                        psi.FileName = linuxRunner.Path;
                        psi.ArgumentList.Clear();
                        psi.ArgumentList.Add("run");
                        psi.ArgumentList.Add(gameExe);
                        psi.EnvironmentVariables["STEAM_COMPAT_DATA_PATH"] = prefixPath;
                        psi.EnvironmentVariables["STEAM_COMPAT_CLIENT_INSTALL_PATH"] = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    }
                    else
                    {
                        psi.FileName = linuxRunner.Path;
                        psi.ArgumentList.Clear();
                        psi.ArgumentList.Add(gameExe);
                        psi.EnvironmentVariables["WINEPREFIX"] = prefixPath;
                        psi.EnvironmentVariables["WINEDEBUG"] = "-all";

                        if (isMac)
                        {
                            string runnerRoot = LinuxEngine.GetMacRunnerRoot(linuxRunner.Path, installPath, linuxRunner.TargetFolderName);

                            if (!LinuxEngine.DeployMacRuntime(runnerRoot, prefixPath, workingDir))
                                throw new IOException("Could not configure the macOS compatibility runtime.");

                            var libSearchPaths = LinuxEngine.GetMacLibraryPaths(linuxRunner.Path, installPath, linuxRunner.TargetFolderName);
                            if (libSearchPaths.Any())
                            {
                                psi.EnvironmentVariables["DYLD_FALLBACK_LIBRARY_PATH"] = string.Join(":", libSearchPaths);
                            }

                            string? d3dMetalFramework = LinuxEngine.FindMacD3DMetalFramework(runnerRoot);
                            bool isGptk = !string.IsNullOrEmpty(d3dMetalFramework) ||
                                          (linuxRunner != null && (
                                              linuxRunner.Name.Contains("GPTK", StringComparison.OrdinalIgnoreCase) ||
                                              (!string.IsNullOrEmpty(linuxRunner.TargetFolderName) && linuxRunner.TargetFolderName.Contains("game-porting-toolkit", StringComparison.OrdinalIgnoreCase))
                                          ));

                            if (isGptk)
                            {
                                if (!string.IsNullOrEmpty(d3dMetalFramework))
                                {
                                    string frameworkDir = Path.GetDirectoryName(d3dMetalFramework)!;
                                    string? wineLibDir = Path.GetDirectoryName(frameworkDir);
                                    string dyldFrameworkPath = frameworkDir;
                                    if (!string.IsNullOrEmpty(wineLibDir) && Directory.Exists(wineLibDir))
                                    {
                                        dyldFrameworkPath = $"{frameworkDir}:{wineLibDir}";
                                    }
                                    psi.EnvironmentVariables["DYLD_FRAMEWORK_PATH"] = dyldFrameworkPath;
                                }

                                psi.EnvironmentVariables["WINEDLLOVERRIDES"] = "d3d12=n;d3d12core=n;dxgi=n;d3d11=n,b;d3d10core=n,b";

                                if (!launchPoseMod)
                                {
                                    // Unity games default to Direct3D 11. Apple D3DMetal is an implementation of Direct3D 12 for Metal.
                                    // Pass -force-d3d12 so Unity renders through D3D12/D3DMetal instead of failing on D3D11.
                                    psi.ArgumentList.Add("-force-d3d12");
                                    AppLogger.Log("Launch", "Configured GPTK environment (forced -force-d3d12 and native D3D12 overrides).");
                                }
                                else
                                {
                                    AppLogger.Log("Launch", "PoseMod launch on GPTK: Omitting -force-d3d12 for injector.");
                                }
                            }
                            else
                            {
                                string gameD3D11 = Path.Combine(workingDir, "d3d11.dll");
                                string dxvkD3D11 = Path.Combine(prefixPath, "drive_c", "windows", "system32", "d3d11.dll");
                                string runnerD3D11 = !string.IsNullOrEmpty(runnerRoot) ? Path.Combine(runnerRoot, "d3d11.dll") : "";
                                if (File.Exists(gameD3D11) || File.Exists(dxvkD3D11) || File.Exists(runnerD3D11))
                                {
                                    psi.EnvironmentVariables["WINEDLLOVERRIDES"] = "d3d11=n,b;d3d10core=n,b";
                                }
                            }
                        }
                    }
                    psi.UseShellExecute = false;
                }

                if (isMac)
                {
                    psi.UseShellExecute = false;
                }

                if (isMac && launchPoseMod)
                {
                    try
                    {
                        var proc = LaunchMacPoseModInTerminal(psi, workingDir, installPath);
                        LastError = proc == null ? I18n.T("ErrLaunch") : null;
                        if (proc != null)
                        {
                            AppLogger.Log("Launch", $"PoseMod debug terminal successfully opened in Terminal.app with PID {proc.Id}");
                        }
                        return proc;
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Error("Launch", "Failed to launch PoseMod in Terminal.app", ex);
                        LastError = I18n.T("ErrLaunch") + ex.Message;
                        StatusChanged?.Invoke(this, LastError);
                        return null;
                    }
                }

                try
                {
                    AppLogger.Log("Launch", $"Starting process: FileName='{psi.FileName}', Arguments='{string.Join(" ", psi.ArgumentList)}', WorkingDir='{psi.WorkingDirectory}'");
                    var proc = Process.Start(psi);
                    LastError = proc == null ? I18n.T("ErrLaunch") : null;
                    if (proc != null)
                    {
                        AppLogger.Log("Launch", $"Process successfully created with PID {proc.Id}");
                    }
                    return proc;
                }
                catch (Exception ex)
                {
                    AppLogger.Error("Launch", "Failed to start process", ex);
                    LastError = I18n.T("ErrLaunch") + ex.Message;
                    StatusChanged?.Invoke(this, LastError);
                    return null;
                }
            }
            else
            {
                AppLogger.Error("Launch", $"Executable not found in game directory: '{gameDir}'");
                LastError = I18n.T("MissingExe");
                StatusChanged?.Invoke(this, LastError);
                return null;
            }
        }

        private static string? FindMacHelper(string fileName, string installPath)
        {
            string[] candidates = new[]
            {
                Path.Combine(AppContext.BaseDirectory, fileName),
                Path.Combine(AppContext.BaseDirectory, "..", "MacOS", fileName),
                Path.Combine(AppContext.BaseDirectory, "..", "Resources", fileName),
                Path.Combine(Directory.GetCurrentDirectory(), fileName),
                Path.Combine(installPath, fileName),
                Path.Combine(installPath, "runner", fileName),
                Path.Combine(installPath, "vers", "runner", fileName),
                "/Applications/YSLauncher.app/Contents/MacOS/" + fileName
            };
            foreach (var path in candidates)
            {
                try
                {
                    if (File.Exists(path)) return Path.GetFullPath(path);
                }
                catch { }
            }
            return null;
        }

        private static Process? LaunchMacPoseModInTerminal(ProcessStartInfo psi, string workingDir, string installPath)
        {
            string scriptDir = Path.Combine(Path.GetTempPath(), "YSLauncher");
            Directory.CreateDirectory(scriptDir);
            string scriptPath = Path.Combine(scriptDir, "posemod_debug.command");

            var sb = new StringBuilder();
            sb.AppendLine("#!/bin/bash");
            sb.AppendLine("printf '\\033]2;YSLauncher - PoseMod (Wine)\\007'");
            sb.AppendLine($"cd \"{workingDir}\"");

            foreach (var key in psi.EnvironmentVariables.Keys)
            {
                string? k = key?.ToString();
                if (!string.IsNullOrEmpty(k))
                {
                    string? v = psi.EnvironmentVariables[k];
                    sb.AppendLine($"export {k}=\"{v?.Replace("\"", "\\\"")}\"");
                }
            }

            var cmdParts = new List<string> { $"\"{psi.FileName}\"" };
            foreach (var arg in psi.ArgumentList)
            {
                cmdParts.Add($"\"{arg.Replace("\"", "\\\"")}\"");
            }
            sb.AppendLine(string.Join(" ", cmdParts));
            sb.AppendLine("osascript -e 'tell application \"Terminal\"' -e 'repeat with w in (every window)' -e 'try' -e 'if name of w contains \"PoseMod\" or name of w contains \"posemod\" then close w saving no' -e 'end try' -e 'end repeat' -e 'if (count of windows) = 0 then quit' -e 'end tell' 2>/dev/null &");
            sb.AppendLine("exit 0");

            File.WriteAllText(scriptPath, sb.ToString());

            try
            {
                var chmodPsi = new ProcessStartInfo
                {
                    FileName = "chmod",
                    ArgumentList = { "+x", scriptPath },
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var cp = Process.Start(chmodPsi);
                cp?.WaitForExit(2000);
            }
            catch { }

            AppLogger.Log("Launch", $"Created macOS PoseMod debug script at '{scriptPath}'. Opening in Terminal.app...");

            var openPsi = new ProcessStartInfo
            {
                FileName = "open",
                ArgumentList = { "-a", "Terminal", scriptPath },
                UseShellExecute = false
            };
            return Process.Start(openPsi);
        }

        public async Task StopCompatibilityProcessesAsync(string installPath, LinuxRunner runner, CancellationToken cancellationToken = default)
        {
            string prefixRoot = GetPrefixPath(installPath, runner);
            string winePrefix = runner.IsProton ? Path.Combine(prefixRoot, "pfx") : prefixRoot;
            string? runnerDirectory = Path.GetDirectoryName(runner.Path);
            string[] candidates = runner.IsProton
                ? new[]
                {
                    Path.Combine(runnerDirectory ?? string.Empty, "files", "bin", "wineserver"),
                    Path.Combine(runnerDirectory ?? string.Empty, "dist", "bin", "wineserver")
                }
                : new[]
                {
                    Path.Combine(runnerDirectory ?? string.Empty, "wineserver")
                };

            string? wineServer = candidates.FirstOrDefault(File.Exists);
            if (wineServer == null)
                throw new FileNotFoundException("Could not locate the Wine server for the selected runner.");

            await RunWineServerCommandAsync(wineServer, winePrefix, "-k", cancellationToken);
            await RunWineServerCommandAsync(wineServer, winePrefix, "-w", cancellationToken);
        }

        private static async Task RunWineServerCommandAsync(string wineServer, string winePrefix, string argument, CancellationToken cancellationToken)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = wineServer,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add(argument);
            startInfo.EnvironmentVariables["WINEPREFIX"] = winePrefix;

            using var process = Process.Start(startInfo) ?? throw new IOException("Could not start the Wine server command.");
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0)
                throw new IOException($"Wine server command '{argument}' failed with exit code {process.ExitCode}.");
        }

        
        public static int? GetPeYear(string exePath)
        {
            try
            {
                if (!File.Exists(exePath)) return null;
                using var fs = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var br = new BinaryReader(fs);
                if (fs.Length < 0x40) return null;
                if (br.ReadUInt16() != 0x5A4D) return null;
                fs.Seek(0x3C, SeekOrigin.Begin);
                uint peOffset = br.ReadUInt32();
                if (peOffset + 8 > fs.Length) return null;
                fs.Seek(peOffset, SeekOrigin.Begin);
                if (br.ReadUInt32() != 0x00004550) return null;
                fs.Seek(4, SeekOrigin.Current);
                uint timeDateStamp = br.ReadUInt32();
                var dt = DateTimeOffset.FromUnixTimeSeconds(timeDateStamp).UtcDateTime;
                if (dt.Year >= 2014 && dt.Year <= DateTime.UtcNow.Year + 1) return dt.Year;
            }
            catch (Exception ex)
            {
                AppLogger.Warning("VersionDetection", $"Could not read the PE timestamp from '{exePath}'.", ex);
            }
            return null;
        }

        public DateTime? GetLocalBuildDateFromDirectory(string gameDir)
        {
            if (string.IsNullOrWhiteSpace(gameDir) || !Directory.Exists(gameDir)) return null;

            try
            {
                string cleanDir = Path.GetFullPath(gameDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                DateTime dirWrite = Directory.GetLastWriteTimeUtc(cleanDir);
                if (_buildDateCache.TryGetValue(cleanDir, out var cached) && cached.dirWrite == dirWrite)
                {
                    return cached.date;
                }

                DateTime? detected = DetectLocalBuildDateInternal(cleanDir);
                _buildDateCache[cleanDir] = (dirWrite, detected);
                return detected;
            }
            catch (Exception ex)
            {
                AppLogger.Warning("VersionDetection", $"Could not inspect build date under '{gameDir}'.", ex);
                return null;
            }
        }

        private DateTime? DetectLocalBuildDateInternal(string gameDir)
        {
            try
            {
                string? gameExe = FindExecutable(gameDir);
                if (gameExe == null) return null;

                string exeDir = Path.GetDirectoryName(gameExe) ?? gameDir;

                // Check ys_version.json (fastest and most accurate for modern builds)
                string[] jsonCandidates = new[]
                {
                    Path.Combine(exeDir, "ys_version.json"),
                    Path.Combine(gameDir, "ys_version.json")
                };
                foreach (var jf in jsonCandidates)
                {
                    if (File.Exists(jf))
                    {
                        try
                        {
                            string json = File.ReadAllText(jf);
                            var mDate = Regex.Match(json, @"""BuildDate""\s*:\s*""(\d{4})-(\d{2})-(\d{2})""");
                            if (mDate.Success)
                            {
                                int jy = int.Parse(mDate.Groups[1].Value);
                                int jm = int.Parse(mDate.Groups[2].Value);
                                int jd = int.Parse(mDate.Groups[3].Value);
                                if (jy >= 2014 && jy <= 2035 && jm >= 1 && jm <= 12 && jd >= 1 && jd <= 31)
                                {
                                    return new DateTime(jy, jm, jd);
                                }
                            }
                        }
                        catch { }
                    }
                }

                string exeNameWithoutExt = Path.GetFileNameWithoutExtension(gameExe);
                string dataDir = Path.Combine(exeDir, $"{exeNameWithoutExt}_Data");
                if (!Directory.Exists(dataDir))
                {
                    dataDir = Path.Combine(exeDir, "YandereSimulator_Data");
                }

                if (Directory.Exists(dataDir))
                {
                    string[] candidateFiles = new[]
                    {
                        Path.Combine(dataDir, "level0"),
                        Path.Combine(dataDir, "level1"),
                        Path.Combine(dataDir, "globalgamemanagers.assets"),
                        Path.Combine(dataDir, "sharedassets0.assets")
                    };

                    var dateRegex = new Regex(@"(January|February|March|April|May|June|July|August|September|October|November|December)\s+(\d{1,2})(?:st|nd|rd|th)?,\s*(\d{4})", RegexOptions.IgnoreCase);

                    foreach (var candidate in candidateFiles)
                    {
                        if (File.Exists(candidate))
                        {
                            var fi = new FileInfo(candidate);
                            if (fi.Length <= 15 * 1024 * 1024)
                            {
                                byte[] bytes = File.ReadAllBytes(candidate);
                                string content = System.Text.Encoding.ASCII.GetString(bytes);
                                var match = dateRegex.Match(content);
                                if (match.Success)
                                {
                                    string monthName = match.Groups[1].Value.ToLowerInvariant();
                                    int day = int.Parse(match.Groups[2].Value);
                                    int year = int.Parse(match.Groups[3].Value);

                                    int month = monthName switch
                                    {
                                        "january" => 1,
                                        "february" or "februrary" => 2,
                                        "march" => 3,
                                        "april" => 4,
                                        "may" => 5,
                                        "june" => 6,
                                        "july" => 7,
                                        "august" => 8,
                                        "september" => 9,
                                        "october" => 10,
                                        "november" => 11,
                                        "december" => 12,
                                        _ => 0
                                    };

                                    if (month > 0 && day >= 1 && day <= 31 && year >= 2014 && year <= 2035)
                                    {
                                        return new DateTime(year, month, day);
                                    }
                                }
                            }
                        }
                    }

                    var watermarkRegex = new Regex(@"(?:This\s+build\s+is\s+from|build\s+is\s+from|build\s+from)\s+(January|February|March|April|May|June|July|August|September|October|November|December)\s+(\d{1,2})(?:st|nd|rd|th)?(?:\s*,?\s*(\d{4}))?", RegexOptions.IgnoreCase);
                    byte[] watermarkProbe = System.Text.Encoding.ASCII.GetBytes("build is from");

                    var levelFiles = new List<string>();
                    foreach (var f in Directory.GetFiles(dataDir, "level*"))
                    {
                        if (f.EndsWith(".resS", StringComparison.OrdinalIgnoreCase)) continue;
                        levelFiles.Add(f);
                    }
                    levelFiles.Sort((a, b) =>
                    {
                        bool a12 = Path.GetFileName(a).Equals("level12", StringComparison.OrdinalIgnoreCase);
                        bool b12 = Path.GetFileName(b).Equals("level12", StringComparison.OrdinalIgnoreCase);
                        if (a12 && !b12) return -1;
                        if (!a12 && b12) return 1;
                        return 0;
                    });

                    const int streamBufSize = 512 * 1024;
                    byte[] streamBuf = new byte[streamBufSize];

                    foreach (var fpath in levelFiles)
                    {
                        var fi = new FileInfo(fpath);
                        if (fi.Length > 90 * 1024 * 1024) continue;

                        using var fs = new FileStream(fpath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, streamBufSize);
                        int overlap = 256;
                        int bytesInBuffer = 0;

                        while (true)
                        {
                            int bytesRead = fs.Read(streamBuf, bytesInBuffer, streamBufSize - bytesInBuffer);
                            if (bytesRead <= 0) break;
                            int totalInBuf = bytesInBuffer + bytesRead;

                            int probeIdx = streamBuf.AsSpan(0, totalInBuf).IndexOf(watermarkProbe);
                            if (probeIdx != -1)
                            {
                                int start = Math.Max(0, probeIdx - 50);
                                int len = Math.Min(totalInBuf - start, 200);
                                string window = System.Text.Encoding.ASCII.GetString(streamBuf, start, len);
                                var m = watermarkRegex.Match(window);
                                if (m.Success)
                                {
                                    string monthName = m.Groups[1].Value.ToLowerInvariant();
                                    int dNum = int.Parse(m.Groups[2].Value);
                                    int? yNum = m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : null;

                                    int mNum = monthName switch
                                    {
                                        "january" => 1,
                                        "february" or "februrary" => 2,
                                        "march" => 3,
                                        "april" => 4,
                                        "may" => 5,
                                        "june" => 6,
                                        "july" => 7,
                                        "august" => 8,
                                        "september" => 9,
                                        "october" => 10,
                                        "november" => 11,
                                        "december" => 12,
                                        _ => 0
                                    };

                                    if (!yNum.HasValue)
                                    {
                                        var fTime = File.GetLastWriteTimeUtc(fpath);
                                        if (fTime.Year >= 2015 && fTime.Year <= 2021)
                                        {
                                            yNum = fTime.Year;
                                        }
                                        else
                                        {
                                            var exeTime = File.GetLastWriteTimeUtc(gameExe);
                                            if (exeTime.Year >= 2015 && exeTime.Year <= 2021)
                                            {
                                                yNum = exeTime.Year;
                                            }
                                            else
                                            {
                                                yNum = GetPeYear(gameExe);
                                            }
                                        }
                                    }

                                    if (mNum > 0 && dNum >= 1 && dNum <= 31 && yNum.HasValue && yNum.Value >= 2014 && yNum.Value <= 2035)
                                    {
                                        return new DateTime(yNum.Value, mNum, dNum);
                                    }
                                }
                            }

                            if (totalInBuf >= overlap)
                            {
                                Array.Copy(streamBuf, totalInBuf - overlap, streamBuf, 0, overlap);
                                bytesInBuffer = overlap;
                            }
                            else
                            {
                                bytesInBuffer = 0;
                            }
                        }
                    }

                    var legacyExeTime = File.GetLastWriteTimeUtc(gameExe);
                    if (legacyExeTime.Year >= 2014 && legacyExeTime.Year <= 2015)
                    {
                        return new DateTime(legacyExeTime.Year, legacyExeTime.Month, legacyExeTime.Day);
                    }
                }

                // Check folder or exe name date parsing (e.g. YandereSimMay7th)
                string folderName = Path.GetFileName(gameDir);
                var (fy, fm, fd) = ParseDateFromVersion(folderName);
                if (fm == 0 || fd == 0)
                {
                    (fy, fm, fd) = ParseDateFromVersion(Path.GetFileNameWithoutExtension(gameExe));
                }

                if (fm > 0 && fd > 0)
                {
                    if (fy >= 2014 && fy <= 2035)
                    {
                        return new DateTime(fy, fm, fd);
                    }

                    var exeTime = File.GetLastWriteTimeUtc(gameExe);
                    int inferredYear = (exeTime.Year >= 2014 && exeTime.Year <= 2035) ? exeTime.Year : (GetPeYear(gameExe) ?? 0);
                    if (inferredYear >= 2014 && inferredYear <= 2035)
                    {
                        return new DateTime(inferredYear, fm, fd);
                    }
                }

                return null;
            }
            catch (Exception ex)
            {
                AppLogger.Warning("VersionDetection", $"Could not detect the build date under '{gameDir}'.", ex);
            }

            return null;
        }

        public DateTime? GetLocalBuildDate(string installPath, string version)
        {
            string? gameDir = GetGameDirectory(installPath, version);
            return !string.IsNullOrEmpty(gameDir) ? GetLocalBuildDateFromDirectory(gameDir) : null;
        }

        public Dictionary<string, string> GetInstalledVersionsMap(string installPath, bool forceRefresh = false)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(installPath) || !Directory.Exists(installPath)) return map;

            try
            {
                string cleanRoot = Path.GetFullPath(installPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                DateTime rootWrite = Directory.GetLastWriteTimeUtc(cleanRoot);
                string versDir = Path.Combine(cleanRoot, "vers");
                DateTime versWrite = Directory.Exists(versDir) ? Directory.GetLastWriteTimeUtc(versDir) : DateTime.MinValue;
                if (!forceRefresh && _installedMapCache.TryGetValue(cleanRoot, out var cached) && cached.pathWrite == rootWrite && cached.versWrite == versWrite)
                {
                    return cached.map;
                }

                void RegisterDir(string dir)
                {
                    if (!Directory.Exists(dir)) return;
                    string? exe = FindExecutable(dir);
                    if (exe == null) return;

                    string folderName = Path.GetFileName(dir);
                    map[folderName] = dir;

                    var bDate = GetLocalBuildDateFromDirectory(dir);
                    if (bDate != null)
                    {
                        if (bDate.Value.Year >= 2024)
                        {
                            if (!map.ContainsKey("Latest")) map["Latest"] = dir;
                        }
                        string tag = $"ys_{bDate.Value.Year}_{bDate.Value.Month:D2}_{bDate.Value.Day:D2}";
                        map[tag] = dir;
                        string tagHyphen = $"ys{bDate.Value.Year}-{bDate.Value.Month:D2}-{bDate.Value.Day:D2}";
                        map[tagHyphen] = dir;
                    }
                    else
                    {
                        var (py, pm, pd) = ParseDateFromVersion(folderName);
                        if (py == 0 && pm > 0 && pd > 0)
                        {
                            var exeTime = File.GetLastWriteTimeUtc(exe);
                            int inferredYear = (exeTime.Year >= 2014 && exeTime.Year <= 2035) ? exeTime.Year : (GetPeYear(exe) ?? 0);
                            if (inferredYear >= 2014 && inferredYear <= 2035) py = inferredYear;
                        }

                        if (py > 0 && pm > 0 && pd > 0)
                        {
                            if (py >= 2024 && !map.ContainsKey("Latest")) map["Latest"] = dir;
                            string tag = $"ys_{py}_{pm:D2}_{pd:D2}";
                            map[tag] = dir;
                            string tagHyphen = $"ys{py}-{pm:D2}-{pd:D2}";
                            map[tagHyphen] = dir;
                        }
                    }
                }

                bool IsIgnoredDir(string dirPath)
                {
                    string name = Path.GetFileName(dirPath);
                    if (string.IsNullOrEmpty(name)) return true;
                    if (name.StartsWith(".")) return true;
                    return name.Equals("prefix", StringComparison.OrdinalIgnoreCase) ||
                           name.Equals("runner", StringComparison.OrdinalIgnoreCase) ||
                           name.Equals("dosdevices", StringComparison.OrdinalIgnoreCase) ||
                           name.Equals("proc", StringComparison.OrdinalIgnoreCase) ||
                           name.Equals("sys", StringComparison.OrdinalIgnoreCase) ||
                           name.Equals("dev", StringComparison.OrdinalIgnoreCase);
                }

                var visitedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                void DiscoverGameDirectories(string currentDir, int depth, int maxDepth)
                {
                    if (depth > maxDepth || !Directory.Exists(currentDir)) return;
                    string full = Path.GetFullPath(currentDir);
                    if (!visitedDirs.Add(full)) return;

                    // If this directory directly contains a game executable, it is a game installation!
                    if (FindDirectExecutable(full) != null)
                    {
                        RegisterDir(full);
                        return; // Do not scan inside a game installation's asset folders
                    }

                    try
                    {
                        foreach (var sub in Directory.GetDirectories(full))
                        {
                            if (IsIgnoredDir(sub)) continue;

                            if (FindDirectExecutable(sub) != null)
                            {
                                RegisterDir(sub);
                            }
                            else if (depth < maxDepth)
                            {
                                DiscoverGameDirectories(sub, depth + 1, maxDepth);
                            }
                            else
                            {
                                string? nestedExe = FindExecutable(sub);
                                if (nestedExe != null)
                                {
                                    RegisterDir(sub);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Warning("Discovery", $"Could not scan directory '{full}'.", ex);
                    }
                }

                if (FindDirectExecutable(cleanRoot) != null)
                {
                    RegisterDir(cleanRoot);
                }
                else
                {
                    DiscoverGameDirectories(cleanRoot, 0, 2);
                }

                _installedMapCache[cleanRoot] = (rootWrite, versWrite, map);
                return map;
            }
            catch (Exception ex)
            {
                AppLogger.Warning("Discovery", $"Could not build the installed-version map for '{installPath}'.", ex);
                return map;
            }
        }

        public bool MatchesInstalledMap(string catalogVersion, Dictionary<string, string> map)
        {
            if (map == null || map.Count == 0) return false;
            if (catalogVersion.Equals("Latest", StringComparison.OrdinalIgnoreCase)) return map.ContainsKey("Latest");

            if (map.ContainsKey(catalogVersion)) return true;

            string cleanName = Path.GetFileNameWithoutExtension(catalogVersion).Replace(".rz", "");
            if (map.ContainsKey(cleanName)) return true;

            var (y, m, d) = ParseDateFromVersion(catalogVersion);
            if (y > 0 && m > 0 && d > 0)
            {
                string tag1 = $"ys_{y}_{m:D2}_{d:D2}";
                if (map.ContainsKey(tag1)) return true;
                string tag2 = $"ys{y}-{m:D2}-{d:D2}";
                if (map.ContainsKey(tag2)) return true;
            }

            return false;
        }

        public List<string> GetLocalInstalledVersions(string installPath)
        {
            var map = GetInstalledVersionsMap(installPath);
            var list = new List<string>();
            foreach (var key in map.Keys)
            {
                if (key.Equals("Latest", StringComparison.OrdinalIgnoreCase) || key.StartsWith("ys_", StringComparison.OrdinalIgnoreCase))
                {
                    if (!list.Contains(key, StringComparer.OrdinalIgnoreCase))
                        list.Add(key);
                }
            }
            return list;
        }

        public string? FindMostRecentLocalVersion(string installPath)
        {
            var locals = GetLocalInstalledVersions(installPath);
            if (locals.Count == 0) return null;

            if (locals.Contains("Latest", StringComparer.OrdinalIgnoreCase))
                return "Latest";

            string? best = null;
            DateTime? bestDate = null;

            foreach (var v in locals)
            {
                var (y, m, d) = ParseDateFromVersion(v);
                if (y > 0 && m > 0 && d > 0)
                {
                    var dt = new DateTime(y, m, d);
                    if (bestDate == null || dt > bestDate)
                    {
                        bestDate = dt;
                        best = v;
                    }
                }
                else if (best == null)
                {
                    best = v;
                }
            }

            return best ?? locals.FirstOrDefault();
        }

        public bool IsGameInstalled(string installPath, string version)
        {
            if (string.IsNullOrWhiteSpace(installPath)) return false;
            var map = GetInstalledVersionsMap(installPath);
            if (version == "Latest")
            {
                return map.ContainsKey("Latest");
            }
            return MatchesInstalledMap(version, map);
        }

        public List<DiscoveredGameVersion> ScanForVersionsToOrganize(string sourcePath, string targetLibraryPath)
        {
            var results = new List<DiscoveredGameVersion>();
            if (string.IsNullOrWhiteSpace(sourcePath) || !Directory.Exists(sourcePath))
                return results;

            string targetVersDir = OperatingSystem.IsWindows()
                ? targetLibraryPath
                : Path.Combine(targetLibraryPath, "vers");
            var seenSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void CheckAndAdd(string dir)
            {
                if (!Directory.Exists(dir)) return;
                string cleanDir = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (seenSources.Contains(cleanDir)) return;

                string name = Path.GetFileName(cleanDir);
                if (name.Equals("vers", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("prefix", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("runner", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("dosdevices", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                if (FindExecutable(cleanDir) == null) return;

                if (Directory.Exists(targetVersDir))
                {
                    string cleanTargetVers = Path.GetFullPath(targetVersDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    var parent = Directory.GetParent(cleanDir);
                    if (parent != null && string.Equals(Path.GetFullPath(parent.FullName).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), cleanTargetVers, StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }
                }

                seenSources.Add(cleanDir);
                var dt = GetLocalBuildDateFromDirectory(cleanDir);
                bool isLatest = dt != null && dt.Value.Year >= 2024;
                string targetSub;
                string summary;

                if (isLatest)
                {
                    targetSub = "Latest";
                    summary = $"{name} -> {I18n.T("LatestVersion")}" + (dt.HasValue ? $" ({dt.Value:dd/MM/yyyy})" : "");
                }
                else if (dt != null)
                {
                    targetSub = $"ys_{dt.Value.Year}_{dt.Value.Month:D2}_{dt.Value.Day:D2}";
                    summary = $"{name} -> {dt.Value:dd/MM/yyyy} ({targetSub})";
                }
                else
                {
                    targetSub = name;
                    summary = $"{name} -> {targetSub}";
                }

                results.Add(new DiscoveredGameVersion
                {
                    SourcePath = cleanDir,
                    FolderName = name,
                    BuildDate = dt,
                    TargetSubfolderName = targetSub,
                    DisplaySummary = summary,
                    IsLatest = isLatest
                });
            }

            if (File.Exists(Path.Combine(sourcePath, ExecutableName)))
            {
                CheckAndAdd(sourcePath);
            }

            try
            {
                foreach (var sub in Directory.GetDirectories(sourcePath))
                {
                    CheckAndAdd(sub);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warning("Organize", $"Could not scan subdirectories under '{sourcePath}'.", ex);
            }

            string srcVers = Path.Combine(sourcePath, "vers");
            if (Directory.Exists(srcVers))
            {
                try
                {
                    foreach (var sub in Directory.GetDirectories(srcVers))
                    {
                        CheckAndAdd(sub);
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Warning("Organize", $"Could not scan version directories under '{srcVers}'.", ex);
                }
            }

            return results;
        }

        public bool ExecuteOrganize(List<DiscoveredGameVersion> items, string targetLibraryPath, out string error)
        {
            error = "";
            if (string.IsNullOrWhiteSpace(targetLibraryPath))
            {
                error = "Target library path is empty.";
                return false;
            }

            try
            {
                string targetBase = OperatingSystem.IsWindows()
                    ? targetLibraryPath
                    : Path.Combine(targetLibraryPath, "vers");
                Directory.CreateDirectory(targetBase);

                foreach (var item in items)
                {
                    if (!Directory.Exists(item.SourcePath)) continue;

                    string destDir = Path.Combine(targetBase, item.TargetSubfolderName);
                    string cleanSrc = Path.GetFullPath(item.SourcePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    string cleanDest = Path.GetFullPath(destDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                    if (string.Equals(cleanSrc, cleanDest, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (cleanDest.StartsWith(cleanSrc + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (Directory.Exists(cleanDest))
                    {
                        int counter = 2;
                        while (Directory.Exists(cleanDest + $"_v{counter}"))
                        {
                            counter++;
                        }
                        cleanDest = cleanDest + $"_v{counter}";
                    }

                    try
                    {
                        Directory.Move(cleanSrc, cleanDest);
                    }
                    catch (IOException ex)
                    {
                        AppLogger.Warning("Organize", $"Direct move from '{cleanSrc}' to '{cleanDest}' failed; using copy fallback.", ex);
                        CopyDirectoryRecursive(cleanSrc, cleanDest);
                        Directory.Delete(cleanSrc, true);
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                AppLogger.Error("Organize", $"Could not organize versions into '{targetLibraryPath}'.", ex);
                return false;
            }
        }

        private static void CopyDirectoryRecursive(string sourceDir, string targetDir)
        {
            Directory.CreateDirectory(targetDir);
            foreach (string file in Directory.GetFiles(sourceDir))
            {
                string targetFile = Path.Combine(targetDir, Path.GetFileName(file));
                try
                {
                    if (File.Exists(targetFile))
                    {
                        File.SetAttributes(targetFile, FileAttributes.Normal);
                    }
                    File.Copy(file, targetFile, true);
                }
                catch (Exception ex)
                {
                    AppLogger.Warning("Copy", $"Failed to copy '{file}' to '{targetFile}'.", ex);
                }
            }
            foreach (string dir in Directory.GetDirectories(sourceDir))
            {
                string targetSubDir = Path.Combine(targetDir, Path.GetFileName(dir));
                CopyDirectoryRecursive(dir, targetSubDir);
            }
        }

        private static void MergeDirectoryNonDestructive(string sourceDir, string targetDir)
        {
            Directory.CreateDirectory(targetDir);
            foreach (string file in Directory.GetFiles(sourceDir))
            {
                string targetFile = Path.Combine(targetDir, Path.GetFileName(file));
                if (!File.Exists(targetFile))
                {
                    try
                    {
                        File.Copy(file, targetFile, overwrite: false);
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Warning("PoseMod", $"Could not merge file '{file}' to '{targetFile}'.", ex);
                    }
                }
            }
            foreach (string dir in Directory.GetDirectories(sourceDir))
            {
                string targetSubDir = Path.Combine(targetDir, Path.GetFileName(dir));
                MergeDirectoryNonDestructive(dir, targetSubDir);
            }
        }

        private static void TryDeleteFile(string path, string tag)
        {
            if (!File.Exists(path)) return;

            try
            {
                File.Delete(path);
            }
            catch (Exception ex)
            {
                AppLogger.Warning(tag, $"Could not delete '{path}'.", ex);
            }
        }

        private static void TryDeleteDirectory(string path, string tag)
        {
            if (!Directory.Exists(path)) return;

            try
            {
                Directory.Delete(path, true);
            }
            catch (Exception ex)
            {
                AppLogger.Warning(tag, $"Could not delete directory '{path}'.", ex);
            }
        }

        private static void CommitStagedDirectory(string stagingDir, string destinationDir)
        {
            if (!Directory.Exists(destinationDir))
            {
                Directory.Move(stagingDir, destinationDir);
                return;
            }

            string backupDir = destinationDir + $".backup-{Guid.NewGuid():N}";
            Directory.Move(destinationDir, backupDir);
            try
            {
                Directory.Move(stagingDir, destinationDir);
            }
            catch (Exception installException)
            {
                try
                {
                    if (!Directory.Exists(destinationDir) && Directory.Exists(backupDir))
                        Directory.Move(backupDir, destinationDir);
                }
                catch (Exception restoreException)
                {
                    throw new AggregateException("Could not install the new version or restore the previous version.", installException, restoreException);
                }
                throw;
            }

            PreservePoseMod(backupDir, destinationDir);

            TryDeleteDirectory(backupDir, "InstallCleanup");
        }

        private static void PreservePoseMod(string backupDir, string destinationDir)
        {
            try
            {
                if (!Directory.Exists(backupDir) || !Directory.Exists(destinationDir)) return;

                // Root Posemod directory (Posemod / PoseMod)
                string? srcPoseModDir = Directory.GetDirectories(backupDir)
                    .FirstOrDefault(d => string.Equals(Path.GetFileName(d), "Posemod", StringComparison.OrdinalIgnoreCase) ||
                                         string.Equals(Path.GetFileName(d), "PoseMod", StringComparison.OrdinalIgnoreCase));

                if (!string.IsNullOrEmpty(srcPoseModDir) && Directory.Exists(srcPoseModDir))
                {
                    string dirName = Path.GetFileName(srcPoseModDir);
                    string destPoseModDir = Path.Combine(destinationDir, dirName);
                    try
                    {
                        MergeDirectoryNonDestructive(srcPoseModDir, destPoseModDir);
                        AppLogger.Log("PoseMod", $"Preserved root directory: {dirName}");
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Warning("PoseMod", $"Failed to preserve root directory '{dirName}'.", ex);
                    }
                }

                // Root Posemod64.exe executable (Posemod64.exe / PoseMod64.exe)
                string? srcExe = Directory.GetFiles(backupDir, "*.exe")
                    .FirstOrDefault(f => string.Equals(Path.GetFileName(f), "Posemod64.exe", StringComparison.OrdinalIgnoreCase) ||
                                         string.Equals(Path.GetFileName(f), "PoseMod64.exe", StringComparison.OrdinalIgnoreCase) ||
                                         string.Equals(Path.GetFileName(f), "PoseMod.exe", StringComparison.OrdinalIgnoreCase) ||
                                         string.Equals(Path.GetFileName(f), "PoseMod32.exe", StringComparison.OrdinalIgnoreCase));

                if (!string.IsNullOrEmpty(srcExe) && File.Exists(srcExe))
                {
                    string fileName = Path.GetFileName(srcExe);
                    string destExe = Path.Combine(destinationDir, fileName);
                    try
                    {
                        if (!File.Exists(destExe))
                        {
                            File.Copy(srcExe, destExe, overwrite: false);
                            AppLogger.Log("PoseMod", $"Preserved executable: {fileName}");
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Warning("PoseMod", $"Failed to preserve executable '{fileName}'.", ex);
                    }
                }

                // YandereSimulator_Data/StreamingAssets/ directory (merge aditivo não-destrutivo)
                string? srcDataDir = Directory.GetDirectories(backupDir)
                    .FirstOrDefault(d => string.Equals(Path.GetFileName(d), "YandereSimulator_Data", StringComparison.OrdinalIgnoreCase));

                if (!string.IsNullOrEmpty(srcDataDir) && Directory.Exists(srcDataDir))
                {
                    string? srcStreamingAssets = Directory.GetDirectories(srcDataDir)
                        .FirstOrDefault(d => string.Equals(Path.GetFileName(d), "StreamingAssets", StringComparison.OrdinalIgnoreCase));

                    if (!string.IsNullOrEmpty(srcStreamingAssets) && Directory.Exists(srcStreamingAssets))
                    {
                        string destDataDir = Path.Combine(destinationDir, Path.GetFileName(srcDataDir));
                        string destStreamingAssets = Path.Combine(destDataDir, Path.GetFileName(srcStreamingAssets));
                        Directory.CreateDirectory(destStreamingAssets);

                        try
                        {
                            MergeDirectoryNonDestructive(srcStreamingAssets, destStreamingAssets);
                            AppLogger.Log("PoseMod", "Preserved PoseMod files in StreamingAssets non-destructively.");
                        }
                        catch (Exception ex)
                        {
                            AppLogger.Warning("PoseMod", "Failed to preserve StreamingAssets directory.", ex);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warning("PoseMod", "Could not complete PoseMod preservation.", ex);
            }
        }

        public string? BackupPoseMod(string gameDir)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(gameDir) || !Directory.Exists(gameDir) || !HasPoseModFiles(gameDir))
                    return null;

                string backupDir = LauncherConfig.PoseModBackupDir;
                if (Directory.Exists(backupDir))
                {
                    try { Directory.Delete(backupDir, true); } catch { }
                }
                Directory.CreateDirectory(backupDir);

                // Root PoseMod directory
                string? srcPoseModDir = Directory.GetDirectories(gameDir)
                    .FirstOrDefault(d => string.Equals(Path.GetFileName(d), "Posemod", StringComparison.OrdinalIgnoreCase) ||
                                         string.Equals(Path.GetFileName(d), "PoseMod", StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrEmpty(srcPoseModDir) && Directory.Exists(srcPoseModDir))
                {
                    string destPoseMod = Path.Combine(backupDir, Path.GetFileName(srcPoseModDir));
                    CopyDirectoryRecursive(srcPoseModDir, destPoseMod);
                    AppLogger.Log("PoseMod", $"Backed up root PoseMod directory: {Path.GetFileName(srcPoseModDir)}");
                }

                // PoseMod executables
                foreach (var exe in Directory.GetFiles(gameDir, "*.exe"))
                {
                    string fn = Path.GetFileName(exe);
                    if (fn.StartsWith("PoseMod", StringComparison.OrdinalIgnoreCase) ||
                        fn.StartsWith("Posemod", StringComparison.OrdinalIgnoreCase))
                    {
                        File.Copy(exe, Path.Combine(backupDir, fn), true);
                        AppLogger.Log("PoseMod", $"Backed up executable: {fn}");
                    }
                }

                // StreamingAssets contents (PoseMod subfolder, custom textures, scripts, etc.)
                string? streamingAssets = FindStreamingAssetsDirectory(gameDir);
                if (!string.IsNullOrEmpty(streamingAssets) && Directory.Exists(streamingAssets))
                {
                    string destSA = Path.Combine(backupDir, "StreamingAssets");
                    CopyDirectoryRecursive(streamingAssets, destSA);
                    AppLogger.Log("PoseMod", "Backed up StreamingAssets directory.");
                }

                AppLogger.Log("PoseMod", $"PoseMod backup completed at '{backupDir}'.");
                return backupDir;
            }
            catch (Exception ex)
            {
                AppLogger.Warning("PoseMod", "Failed to backup PoseMod before update.", ex);
                return null;
            }
        }

        public bool RestorePoseMod(string newGameDir, string? backupDir = null)
        {
            try
            {
                backupDir ??= LauncherConfig.PoseModBackupDir;
                if (string.IsNullOrWhiteSpace(newGameDir) || !Directory.Exists(newGameDir) ||
                    string.IsNullOrWhiteSpace(backupDir) || !Directory.Exists(backupDir))
                {
                    return false;
                }

                AppLogger.Log("PoseMod", $"Restoring PoseMod from '{backupDir}' into '{newGameDir}'...");

                // Root PoseMod directory
                string? srcPoseModDir = Directory.GetDirectories(backupDir)
                    .FirstOrDefault(d => string.Equals(Path.GetFileName(d), "Posemod", StringComparison.OrdinalIgnoreCase) ||
                                         string.Equals(Path.GetFileName(d), "PoseMod", StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrEmpty(srcPoseModDir) && Directory.Exists(srcPoseModDir))
                {
                    string destPoseMod = Path.Combine(newGameDir, Path.GetFileName(srcPoseModDir));
                    MergeDirectoryNonDestructive(srcPoseModDir, destPoseMod);
                    AppLogger.Log("PoseMod", $"Restored PoseMod root directory: {Path.GetFileName(srcPoseModDir)}");
                }

                // PoseMod executables
                foreach (var exe in Directory.GetFiles(backupDir, "*.exe"))
                {
                    string fn = Path.GetFileName(exe);
                    string destExe = Path.Combine(newGameDir, fn);
                    if (!File.Exists(destExe))
                    {
                        File.Copy(exe, destExe, overwrite: false);
                        AppLogger.Log("PoseMod", $"Restored executable: {fn}");
                    }
                }

                // StreamingAssets merge
                string srcSA = Path.Combine(backupDir, "StreamingAssets");
                if (Directory.Exists(srcSA))
                {
                    string? destSA = FindStreamingAssetsDirectory(newGameDir);
                    if (string.IsNullOrEmpty(destSA))
                    {
                        destSA = Path.Combine(newGameDir, "YandereSimulator_Data", "StreamingAssets");
                        Directory.CreateDirectory(destSA);
                    }
                    MergeDirectoryNonDestructive(srcSA, destSA);
                    AppLogger.Log("PoseMod", "Merged PoseMod StreamingAssets non-destructively.");
                }

                try { Directory.Delete(backupDir, true); } catch { }
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Warning("PoseMod", "Failed to restore PoseMod into new game version.", ex);
                return false;
            }
        }

        public bool DeleteVersion(string installPath, string version, out string error)
        {
            error = string.Empty;
            string? gameDir = GetGameDirectory(installPath, version);
            if (string.IsNullOrWhiteSpace(gameDir) || !Directory.Exists(gameDir)) return true;

            try
            {
                string fullInstall = Path.GetFullPath(installPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string fullGame = Path.GetFullPath(gameDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                if (string.Equals(fullGame, fullInstall, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(Path.GetFileName(fullGame), "vers", StringComparison.OrdinalIgnoreCase))
                {
                    string[] gameFiles = {
                        "YandereSimulator.exe", "UnityPlayer.dll", "UnityCrashHandler64.exe", "UnityCrashHandler32.exe",
                        "d3d11.dll", "d3d10core.dll", "dxvk.conf", "ys_version_bytes.txt", ".vers.log"
                    };
                    foreach (var gf in gameFiles)
                    {
                        string p = Path.Combine(fullGame, gf);
                        if (File.Exists(p)) File.Delete(p);
                    }

                    string[] gameDirs = { "YandereSimulator_Data", "MonoBleedingEdge", "YandereSimulator" };
                    foreach (var gd in gameDirs)
                    {
                        string p = Path.Combine(fullGame, gd);
                        if (Directory.Exists(p)) Directory.Delete(p, true);
                    }
                }
                else
                {
                    Directory.Delete(fullGame, true);
                }
                _buildDateCache.Clear();
                _installedMapCache.Clear();

                try
                {
                    string rootLog = Path.Combine(installPath, ".vers.log");
                    if (File.Exists(rootLog))
                    {
                        var remainingLines = File.ReadAllLines(rootLog)
                            .Where(line =>
                            {
                                var trimmed = line.Trim();
                                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("#")) return true;
                                var parts = trimmed.Split(new[] { '=', ':' }, 2);
                                return !string.Equals(parts[0].Trim(), version, StringComparison.OrdinalIgnoreCase);
                            })
                            .ToList();
                        File.WriteAllLines(rootLog, remainingLines);
                        if (OperatingSystem.IsWindows())
                        {
                            try { File.SetAttributes(rootLog, File.GetAttributes(rootLog) | FileAttributes.Hidden); } catch { }
                        }
                    }
                }
                catch { }

                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                AppLogger.Error("Delete", $"Could not delete version '{version}' at '{gameDir}'.", ex);
                return false;
            }
        }

        public static string? FindStreamingAssetsDirectory(string gameDir)
        {
            if (string.IsNullOrWhiteSpace(gameDir) || !Directory.Exists(gameDir)) return null;

            string[] candidates = new[]
            {
                Path.Combine(gameDir, "YandereSimulator_Data", "StreamingAssets"),
                Path.Combine(gameDir, "StreamingAssets"),
                Path.Combine(gameDir, "Assets", "StreamingAssets"),
                Path.Combine(gameDir, "YandereSimulator", "YandereSimulator_Data", "StreamingAssets")
            };

            foreach (var candidate in candidates)
            {
                if (Directory.Exists(candidate)) return candidate;
            }

            try
            {
                var found = Directory.GetDirectories(gameDir, "StreamingAssets", SearchOption.AllDirectories);
                if (found.Length > 0) return found[0];
            }
            catch { }

            return null;
        }

        public static string GetCustomModeBackupPath(string installPath)
        {
            try
            {
                if (!Directory.Exists(LauncherConfig.ConfigDir)) Directory.CreateDirectory(LauncherConfig.ConfigDir);
                string newPath = LauncherConfig.CustomModeBackupFile;
                string legacyPath = Path.Combine(LauncherConfig.ConfigDir, "cm_backup.zip");
                if (File.Exists(legacyPath) && !File.Exists(newPath))
                {
                    try { File.Move(legacyPath, newPath); } catch { return legacyPath; }
                }
                return newPath;
            }
            catch
            {
                return Path.Combine(installPath, "cmbkp.zip");
            }
        }

        public static readonly DateTime CustomModeReleaseDate = new DateTime(2024, 1, 5);

        public bool VersionSupportsCustomMode(string installPath, string version)
        {
            if (string.Equals(version, "Latest", StringComparison.OrdinalIgnoreCase))
                return true;

            var (year, month, day) = ParseDateFromVersion(version);
            if (year > 0 && month > 0 && day > 0)
            {
                return new DateTime(year, month, day) >= CustomModeReleaseDate;
            }

            var localDate = GetLocalBuildDate(installPath, version);
            if (localDate.HasValue)
            {
                return localDate.Value.Date >= CustomModeReleaseDate;
            }

            string? gameDir = GetGameDirectory(installPath, version);
            if (!string.IsNullOrEmpty(gameDir))
            {
                string? streamingAssets = FindStreamingAssetsDirectory(gameDir);
                if (!string.IsNullOrEmpty(streamingAssets) && Directory.Exists(streamingAssets))
                {
                    if (Directory.Exists(Path.Combine(streamingAssets, "CustomMode")) ||
                        File.Exists(Path.Combine(streamingAssets, "JSON", "Custom.json")))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        public string? BackupCustomMode(string installPath, string gameDir)
        {
            try
            {
                string? streamingAssets = FindStreamingAssetsDirectory(gameDir);
                if (string.IsNullOrEmpty(streamingAssets) || !Directory.Exists(streamingAssets))
                {
                    AppLogger.Log("Backup", $"StreamingAssets not found in '{gameDir}', nothing to backup.");
                    return null;
                }

                string backupZipPath = GetCustomModeBackupPath(installPath);
                string bakZipPath = backupZipPath + ".bak";
                bool hadPreviousBackup = File.Exists(backupZipPath);

                if (hadPreviousBackup)
                {
                    try
                    {
                        if (File.Exists(bakZipPath)) File.Delete(bakZipPath);
                        File.Move(backupZipPath, bakZipPath);
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Warning("Backup", "Could not create .bak backup file.", ex);
                    }
                }

                int itemsBackedUp = 0;
                var addedEntries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    using (var zipArchive = ZipFile.Open(backupZipPath, ZipArchiveMode.Create))
                    {
                        string[] dirsToBackup = { "CustomMode", "PortraitsCustom", "SaveData", "SaveFiles" };
                        foreach (var dirName in dirsToBackup)
                        {
                            string sourceDir = Path.Combine(streamingAssets, dirName);
                            if (Directory.Exists(sourceDir))
                            {
                                foreach (var filePath in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
                                {
                                    string relativePath = Path.GetRelativePath(streamingAssets, filePath);
                                    string entryName = relativePath.Replace('\\', '/');
                                    if (addedEntries.Add(entryName))
                                    {
                                        zipArchive.CreateEntryFromFile(filePath, entryName, CompressionLevel.Optimal);
                                        itemsBackedUp++;
                                    }
                                }
                            }
                        }

                        string[] filesToBackup = {
                            Path.Combine("JSON", "Custom.json"),
                            Path.Combine("JSON", "CustomTopics.json"),
                            Path.Combine("JSON", "Misc.json"),
                            "CustomPortrait.png",
                            "CustomPortrait.txt",
                            "SenpaiPortrait.png",
                            "YanderePortrait.png",
                            "EightiesSenpaiPortrait.png"
                        };

                        foreach (var relFile in filesToBackup)
                        {
                            string sourceFile = Path.Combine(streamingAssets, relFile);
                            if (File.Exists(sourceFile))
                            {
                                string entryName = relFile.Replace('\\', '/');
                                if (addedEntries.Add(entryName))
                                {
                                    zipArchive.CreateEntryFromFile(sourceFile, entryName, CompressionLevel.Optimal);
                                    itemsBackedUp++;
                                }
                            }
                        }
                    }

                    if (itemsBackedUp > 0)
                    {
                        try { if (File.Exists(bakZipPath)) File.Delete(bakZipPath); } catch { }
                        AppLogger.Log("Backup", $"Successfully backed up {itemsBackedUp} Custom Mode / Save files to '{backupZipPath}'.");
                        return backupZipPath;
                    }
                    else
                    {
                        if (File.Exists(backupZipPath)) File.Delete(backupZipPath);
                        if (hadPreviousBackup && File.Exists(bakZipPath))
                        {
                            try { File.Move(bakZipPath, backupZipPath); } catch { }
                        }
                        AppLogger.Log("Backup", "No Custom Mode or Save files found to backup.");
                        return null;
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Error("Backup", "Failed to backup Custom Mode files.", ex);
                    if (File.Exists(backupZipPath)) File.Delete(backupZipPath);
                    if (hadPreviousBackup && File.Exists(bakZipPath))
                    {
                        try { File.Move(bakZipPath, backupZipPath); } catch { }
                    }
                    return null;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("Backup", "Failed in BackupCustomMode outer block.", ex);
                return null;
            }
        }

        public bool IsCustomModeSynchronized(string installPath, string version)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(installPath) || !VersionSupportsCustomMode(installPath, version))
                    return true;

                string? gameDir = GetGameDirectory(installPath, version);
                if (string.IsNullOrEmpty(gameDir) || !Directory.Exists(gameDir) || !HasCustomModeFiles(gameDir))
                    return true;

                string backupPath = GetCustomModeBackupPath(installPath);
                if (!File.Exists(backupPath))
                {
                    string globalBackup = LauncherConfig.CustomModeBackupFile;
                    if (File.Exists(globalBackup)) backupPath = globalBackup;
                    else return false;
                }

                DateTime zipLastWrite = File.GetLastWriteTimeUtc(backupPath);
                string? streamingAssets = FindStreamingAssetsDirectory(gameDir);
                if (string.IsNullOrEmpty(streamingAssets) || !Directory.Exists(streamingAssets))
                    return true;

                string[] dirs = { "CustomMode", "PortraitsCustom", "SaveData", "SaveFiles" };
                foreach (var dir in dirs)
                {
                    string p = Path.Combine(streamingAssets, dir);
                    if (Directory.Exists(p))
                    {
                        foreach (var file in Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories))
                        {
                            if (File.GetLastWriteTimeUtc(file) > zipLastWrite) return false;
                        }
                    }
                }

                string[] files = {
                    Path.Combine("JSON", "Custom.json"),
                    Path.Combine("JSON", "CustomTopics.json"),
                    Path.Combine("JSON", "Misc.json"),
                    "CustomPortrait.png",
                    "CustomPortrait.txt",
                    "SenpaiPortrait.png",
                    "YanderePortrait.png",
                    "EightiesSenpaiPortrait.png"
                };
                foreach (var f in files)
                {
                    string fullPath = Path.Combine(streamingAssets, f);
                    if (File.Exists(fullPath) && File.GetLastWriteTimeUtc(fullPath) > zipLastWrite)
                    {
                        return false;
                    }
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        public bool HasCustomModeFiles(string? gameDir)
        {
            if (string.IsNullOrEmpty(gameDir) || !Directory.Exists(gameDir)) return false;
            string? streamingAssets = FindStreamingAssetsDirectory(gameDir);
            if (string.IsNullOrEmpty(streamingAssets) || !Directory.Exists(streamingAssets)) return false;

            string[] dirs = { "CustomMode", "PortraitsCustom", "SaveData", "SaveFiles" };
            foreach (var dir in dirs)
            {
                string p = Path.Combine(streamingAssets, dir);
                if (Directory.Exists(p))
                {
                    try
                    {
                        if (Directory.EnumerateFileSystemEntries(p).Any()) return true;
                    }
                    catch { }
                }
            }

            string[] files = {
                Path.Combine("JSON", "Custom.json"),
                Path.Combine("JSON", "CustomTopics.json"),
                Path.Combine("JSON", "Misc.json"),
                "CustomPortrait.png",
                "CustomPortrait.txt",
                "SenpaiPortrait.png",
                "YanderePortrait.png",
                "EightiesSenpaiPortrait.png"
            };

            foreach (var f in files)
            {
                if (File.Exists(Path.Combine(streamingAssets, f))) return true;
            }

            return false;
        }

        public string? SyncCustomModeBackup(string installPath, string version)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(installPath) || !VersionSupportsCustomMode(installPath, version))
                    return null;

                string? gameDir = GetGameDirectory(installPath, version);
                if (string.IsNullOrEmpty(gameDir) || !Directory.Exists(gameDir) || !HasCustomModeFiles(gameDir))
                    return null;

                string backupPath = GetCustomModeBackupPath(installPath);
                if (!File.Exists(backupPath))
                {
                    return BackupCustomMode(installPath, gameDir);
                }

                DateTime zipLastWrite = File.GetLastWriteTimeUtc(backupPath);
                string? streamingAssets = FindStreamingAssetsDirectory(gameDir);
                if (!string.IsNullOrEmpty(streamingAssets) && Directory.Exists(streamingAssets))
                {
                    bool hasNewerFile = false;
                    string[] dirs = { "CustomMode", "PortraitsCustom", "SaveData", "SaveFiles" };
                    foreach (var dir in dirs)
                    {
                        string p = Path.Combine(streamingAssets, dir);
                        if (Directory.Exists(p))
                        {
                            foreach (var file in Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories))
                            {
                                if (File.GetLastWriteTimeUtc(file) > zipLastWrite)
                                {
                                    hasNewerFile = true;
                                    break;
                                }
                            }
                        }
                        if (hasNewerFile) break;
                    }

                    if (!hasNewerFile)
                    {
                        string[] files = {
                            Path.Combine("JSON", "Custom.json"),
                            Path.Combine("JSON", "CustomTopics.json"),
                            Path.Combine("JSON", "Misc.json"),
                            "CustomPortrait.png",
                            "CustomPortrait.txt",
                            "SenpaiPortrait.png",
                            "YanderePortrait.png",
                            "EightiesSenpaiPortrait.png"
                        };
                        foreach (var f in files)
                        {
                            string fullPath = Path.Combine(streamingAssets, f);
                            if (File.Exists(fullPath) && File.GetLastWriteTimeUtc(fullPath) > zipLastWrite)
                            {
                                hasNewerFile = true;
                                break;
                            }
                        }
                    }

                    if (hasNewerFile)
                    {
                        AppLogger.Log("Backup", "Custom Mode files are newer than backup; updating cmbkp.zip...");
                        return BackupCustomMode(installPath, gameDir);
                    }
                }

                return backupPath;
            }
            catch (Exception ex)
            {
                AppLogger.Error("Backup", "Failed in SyncCustomModeBackup", ex);
                return null;
            }
        }

        public bool RestoreCustomMode(string installPath, string newGameDir, string? backupZipPath = null)
        {
            try
            {
                backupZipPath ??= GetCustomModeBackupPath(installPath);
                if (string.IsNullOrEmpty(backupZipPath) || !File.Exists(backupZipPath))
                {
                    return false;
                }

                string? streamingAssets = FindStreamingAssetsDirectory(newGameDir);
                if (string.IsNullOrEmpty(streamingAssets))
                {
                    streamingAssets = Path.Combine(newGameDir, "YandereSimulator_Data", "StreamingAssets");
                    Directory.CreateDirectory(streamingAssets);
                }

                AppLogger.Log("Restore", $"Restoring Custom Mode files from '{backupZipPath}' to '{streamingAssets}'...");
                int itemsRestored = 0;
                using (var zipArchive = ZipFile.OpenRead(backupZipPath))
                {
                    foreach (var entry in zipArchive.Entries)
                    {
                        if (string.IsNullOrEmpty(entry.Name)) continue;

                        string targetPath = Path.Combine(streamingAssets, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
                        string? parentDir = Path.GetDirectoryName(targetPath);
                        if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir))
                        {
                            Directory.CreateDirectory(parentDir);
                        }

                        entry.ExtractToFile(targetPath, overwrite: true);
                        itemsRestored++;
                    }
                }

                AppLogger.Log("Restore", $"Successfully restored {itemsRestored} Custom Mode / Save files.");
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Error("Restore", "Failed to restore Custom Mode files.", ex);
                return false;
            }
        }

        public bool VerifyAndSyncCustomModeWithHash(string installPath, string version)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(installPath) || !VersionSupportsCustomMode(installPath, version))
                    return false;

                string? gameDir = GetGameDirectory(installPath, version);
                string backupPath = GetCustomModeBackupPath(installPath);
                if (!File.Exists(backupPath))
                {
                    string globalBackup = LauncherConfig.CustomModeBackupFile;
                    if (File.Exists(globalBackup))
                    {
                        backupPath = globalBackup;
                    }
                }

                bool hasGameDir = !string.IsNullOrEmpty(gameDir) && Directory.Exists(gameDir);
                bool hasGameCustom = hasGameDir && HasCustomModeFiles(gameDir);
                bool hasBackupZip = File.Exists(backupPath);

                if (!hasGameCustom && !hasBackupZip)
                {
                    AppLogger.Log("Backup", "VerifyAndSync: No Custom Mode files found in game directory or backup archive.");
                    return false;
                }

                if (hasGameCustom && !hasBackupZip)
                {
                    AppLogger.Log("Backup", "VerifyAndSync: Creating initial Custom Mode backup...");
                    string? created = BackupCustomMode(installPath, gameDir!);
                    return !string.IsNullOrEmpty(created) && File.Exists(created);
                }

                if (!hasGameCustom && hasBackupZip)
                {
                    if (hasGameDir)
                    {
                        AppLogger.Log("Backup", "VerifyAndSync: Restoring Custom Mode backup to current game version...");
                        RestoreCustomMode(installPath, gameDir!, backupPath);
                    }
                    return true;
                }

                // Both game and backup exist: perform SHA-256 verification and synchronization
                string? streamingAssets = FindStreamingAssetsDirectory(gameDir!);
                if (string.IsNullOrEmpty(streamingAssets) || !Directory.Exists(streamingAssets))
                {
                    return hasBackupZip;
                }

                bool filesDiffer = false;
                using (var sha256 = SHA256.Create())
                {
                    using (var zipArchive = ZipFile.OpenRead(backupPath))
                    {
                        var zipEntries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                        foreach (var entry in zipArchive.Entries)
                        {
                            if (string.IsNullOrEmpty(entry.Name)) continue;
                            string entryRelPath = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                            zipEntries.Add(entryRelPath);

                            string diskFile = Path.Combine(streamingAssets, entryRelPath);
                            if (!File.Exists(diskFile))
                            {
                                filesDiffer = true;
                                break;
                            }

                            var fi = new FileInfo(diskFile);
                            if (fi.Length != entry.Length)
                            {
                                filesDiffer = true;
                                break;
                            }

                            byte[] diskHash;
                            using (var diskStream = File.OpenRead(diskFile))
                            {
                                diskHash = sha256.ComputeHash(diskStream);
                            }

                            byte[] zipHash;
                            using (var entryStream = entry.Open())
                            {
                                zipHash = sha256.ComputeHash(entryStream);
                            }

                            if (!diskHash.SequenceEqual(zipHash))
                            {
                                filesDiffer = true;
                                break;
                            }
                        }

                        if (!filesDiffer)
                        {
                            string[] dirs = { "CustomMode", "PortraitsCustom", "SaveData", "SaveFiles" };
                            foreach (var dir in dirs)
                            {
                                string p = Path.Combine(streamingAssets, dir);
                                if (Directory.Exists(p))
                                {
                                    foreach (var file in Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories))
                                    {
                                        string rel = Path.GetRelativePath(streamingAssets, file);
                                        if (!zipEntries.Contains(rel))
                                        {
                                            filesDiffer = true;
                                            break;
                                        }
                                    }
                                }
                                if (filesDiffer) break;
                            }

                            if (!filesDiffer)
                            {
                                string[] files = {
                                    Path.Combine("JSON", "Custom.json"),
                                    Path.Combine("JSON", "CustomTopics.json"),
                                    Path.Combine("JSON", "Misc.json"),
                                    "CustomPortrait.png",
                                    "CustomPortrait.txt",
                                    "SenpaiPortrait.png",
                                    "YanderePortrait.png",
                                    "EightiesSenpaiPortrait.png"
                                };
                                foreach (var f in files)
                                {
                                    string fullPath = Path.Combine(streamingAssets, f);
                                    if (File.Exists(fullPath) && !zipEntries.Contains(f))
                                    {
                                        filesDiffer = true;
                                        break;
                                    }
                                }
                            }
                        }
                    }
                }

                if (!filesDiffer)
                {
                    AppLogger.Log("Backup", "VerifyAndSync: SHA-256 match! Custom Mode files and backup archive are 100% in sync.");
                    return true;
                }

                DateTime zipLastWrite = File.GetLastWriteTimeUtc(backupPath);
                DateTime maxDiskWrite = DateTime.MinValue;

                string[] checkDirs = { "CustomMode", "PortraitsCustom", "SaveData", "SaveFiles" };
                foreach (var dir in checkDirs)
                {
                    string p = Path.Combine(streamingAssets, dir);
                    if (Directory.Exists(p))
                    {
                        foreach (var file in Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories))
                        {
                            var t = File.GetLastWriteTimeUtc(file);
                            if (t > maxDiskWrite) maxDiskWrite = t;
                        }
                    }
                }
                string[] checkFiles = {
                    Path.Combine("JSON", "Custom.json"),
                    Path.Combine("JSON", "CustomTopics.json"),
                    Path.Combine("JSON", "Misc.json"),
                    "CustomPortrait.png",
                    "CustomPortrait.txt",
                    "SenpaiPortrait.png",
                    "YanderePortrait.png",
                    "EightiesSenpaiPortrait.png"
                };
                foreach (var f in checkFiles)
                {
                    string fullPath = Path.Combine(streamingAssets, f);
                    if (File.Exists(fullPath))
                    {
                        var t = File.GetLastWriteTimeUtc(fullPath);
                        if (t > maxDiskWrite) maxDiskWrite = t;
                    }
                }

                if (maxDiskWrite >= zipLastWrite)
                {
                    AppLogger.Log("Backup", "VerifyAndSync: Game files are newer/different; updating backup archive with SHA-256 verification...");
                    string? updated = BackupCustomMode(installPath, gameDir!);
                    return !string.IsNullOrEmpty(updated) && File.Exists(updated);
                }
                else
                {
                    AppLogger.Log("Backup", "VerifyAndSync: Backup archive is newer/different; restoring to game directory...");
                    RestoreCustomMode(installPath, gameDir!, backupPath);
                    return true;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("Backup", "VerifyAndSyncCustomModeWithHash failed.", ex);
                return false;
            }
        }
    }

    public class DiscoveredGameVersion
    {
        public string SourcePath { get; set; } = "";
        public string FolderName { get; set; } = "";
        public DateTime? BuildDate { get; set; }
        public string TargetSubfolderName { get; set; } = "";
        public string DisplaySummary { get; set; } = "";
        public bool IsLatest { get; set; }
    }
