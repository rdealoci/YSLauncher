using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using YSLauncher.Services;

namespace YSLauncher.Platforms.Linux;

    public class LinuxRunner
    {
        public string Name { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public bool IsProton { get; set; }
        public bool IsRecommended { get; set; }
        public bool NeedsDownload { get; set; }
        public string DownloadUrl { get; set; } = string.Empty;
        public string ExpectedSha256 { get; set; } = string.Empty;
        public string TargetFolderName { get; set; } = string.Empty;
        public string? NoteKey { get; set; }
        
        public string DisplayName 
        {
            get
            {
                string suffix = "";
                if (IsRecommended) suffix += I18n.T("Recommended");
                if (!string.IsNullOrEmpty(NoteKey)) suffix += $" ({I18n.T(NoteKey)})";
                if (!NeedsDownload) suffix += I18n.T("Installed");
                return $"{Name}{suffix}";
            }
        }
    }

    public static class LinuxEngine
    {
        public static bool IsLowEndPc()
        {
            if (!OperatingSystem.IsLinux()) return false;
            try
            {
                string meminfo = File.ReadAllText("/proc/meminfo");
                var match = Regex.Match(meminfo, @"MemTotal:\s+(\d+)\s+kB");
                if (match.Success)
                {
                    long kb = long.Parse(match.Groups[1].Value);
                    if (kb < 7500000) return true;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warning("Hardware", "Could not read Linux memory information.", ex);
            }
            return false;
        }

        public static string GetGpuVendor()
        {
            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "lspci",
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };
                process.Start();
                string output = process.StandardOutput.ReadToEnd().ToLower();
                process.WaitForExit();

                if (output.Contains("vga compatible controller") || output.Contains("3d controller"))
                {
                    if (output.Contains("amd") || output.Contains("radeon")) return "AMD";
                    if (output.Contains("nvidia")) return "NVIDIA";
                    if (output.Contains("intel")) return "INTEL";
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warning("Hardware", "Could not detect the GPU vendor with lspci.", ex);
            }
            return "UNKNOWN";
        }

        public static async Task<string?> GetLatestGEProtonUrlAsync()
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("User-Agent", "YSLauncher");
            try
            {
                string json = await client.GetStringAsync("https://api.github.com/repos/GloriousEggroll/proton-ge-custom/releases/latest");
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                {
                    foreach (var asset in assets.EnumerateArray())
                    {
                        if (asset.TryGetProperty("browser_download_url", out var urlElement))
                        {
                            string? url = urlElement.GetString();
                            if (!string.IsNullOrEmpty(url) && url.EndsWith("-x86_64.tar.gz", StringComparison.OrdinalIgnoreCase))
                            {
                                return url;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warning("Runner", "Could not query the latest GE-Proton release.", ex);
            }
            return null;
        }

        public static string GetRunnersDirectory(string installPath)
        {
            string newDir = Path.Combine(installPath, "runner");
            string oldDir = Path.Combine(installPath, "vers", "runner");
            if (!Directory.Exists(newDir) && Directory.Exists(oldDir))
            {
                try
                {
                    Directory.Move(oldDir, newDir);
                    AppLogger.Log("Runner", $"Migrated runner directory from '{oldDir}' to '{newDir}'.");
                    return newDir;
                }
                catch (Exception ex)
                {
                    AppLogger.Warning("Runner", $"Failed to migrate runner directory from '{oldDir}' to '{newDir}'.", ex);
                    return oldDir;
                }
            }
            return newDir;
        }

        public static List<LinuxRunner> ScanRunners(string installPath)
        {
            var runners = new List<LinuxRunner>();
            string localRunnerDir = GetRunnersDirectory(installPath);
            if (OperatingSystem.IsLinux())
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string gpu = GetGpuVendor();

                bool hasLocalGE = false;
                if (Directory.Exists(localRunnerDir))
                {
                    var protonDirs = Directory.GetDirectories(localRunnerDir).Where(d => Path.GetFileName(d).StartsWith("GE-Proton"));
                    foreach (var dir in protonDirs)
                    {
                        string runnerPath = Path.Combine(dir, "proton");
                        if (File.Exists(runnerPath))
                        {
                            runners.Add(new LinuxRunner
                            {
                                Name = Path.GetFileName(dir),
                                Path = runnerPath,
                                IsProton = true,
                                IsRecommended = true
                            });
                            hasLocalGE = true;
                        }
                    }
                }

                if (!hasLocalGE)
                {
                    runners.Add(new LinuxRunner
                    {
                        Name = "GE-Proton",
                        Path = "DOWNLOAD_GE",
                        IsProton = true,
                        IsRecommended = false,
                        NeedsDownload = true
                    });
                }

                var protonPaths = new List<string>
                {
                    Path.Combine(home, ".local", "share", "Steam", "steamapps", "common"),
                    Path.Combine(home, ".steam", "root", "steamapps", "common"),
                    Path.Combine(home, ".local", "share", "Steam", "compatibilitytools.d")
                };

                foreach (var path in protonPaths)
                {
                    if (Directory.Exists(path))
                    {
                        var dirs = Directory.GetDirectories(path).Where(d => 
                            Path.GetFileName(d).StartsWith("Proton", StringComparison.OrdinalIgnoreCase) ||
                            Path.GetFileName(d).StartsWith("GE-Proton", StringComparison.OrdinalIgnoreCase));

                        foreach (var dir in dirs)
                        {
                            string name = Path.GetFileName(dir);
                            string runnerPath = Path.Combine(dir, "proton");
                            
                            if (File.Exists(runnerPath) && !runners.Any(r => r.Path == runnerPath))
                            {
                                bool isGE = name.Contains("GE");
                                bool recommend = !hasLocalGE && ((gpu == "AMD" && isGE) || (gpu == "NVIDIA" && name.Contains("Experimental")));

                                runners.Add(new LinuxRunner
                                {
                                    Name = $"{name}{I18n.T("Steam")}",
                                    Path = runnerPath,
                                    IsProton = true,
                                    IsRecommended = recommend
                                });
                            }
                        }
                    }
                }

                try
                {
                    using var process = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = "which",
                            ArgumentList = { "wine" },
                            RedirectStandardOutput = true,
                            UseShellExecute = false
                        }
                    };
                    process.Start();
                    string winePath = process.StandardOutput.ReadToEnd().Trim();
                    process.WaitForExit();

                    if (!string.IsNullOrEmpty(winePath) && File.Exists(winePath))
                    {
                        runners.Add(new LinuxRunner 
                        { 
                            Name = I18n.T("SystemWine"), 
                            Path = winePath, 
                            IsProton = false,
                            IsRecommended = runners.Count(r => !r.NeedsDownload) == 0
                        });
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Warning("Runner", "Could not locate the system Wine executable.", ex);
                }

                var downloadGE = runners.FirstOrDefault(r => r.Path == "DOWNLOAD_GE");
                if (downloadGE != null && !runners.Any(r => !r.NeedsDownload && r.IsProton))
                {
                    downloadGE.IsRecommended = true;
                }
            }
            else if (OperatingSystem.IsMacOS())
            {
                ScanMacRunners(runners, installPath);
            }

            return runners.OrderBy(r => r.NeedsDownload).ThenByDescending(r => r.IsRecommended).ToList();
        }

        private static void ScanMacRunners(List<LinuxRunner> runners, string installPath)
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string runnersBase = GetRunnersDirectory(installPath);

            bool isArmMac = OperatingSystem.IsMacOS() && (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 || RuntimeInformation.OSArchitecture == Architecture.Arm64);

            string local24Dir = Path.Combine(runnersBase, "Wine-Crossover-24");
            string? bin24 = FindMacWineBinary(local24Dir);
            if (string.IsNullOrEmpty(bin24))
            {
                string legacy24Dir = Path.Combine(installPath, "vers", "runner", "Wine-Crossover-24");
                bin24 = FindMacWineBinary(legacy24Dir);
            }

            if (!string.IsNullOrEmpty(bin24))
            {
                runners.Add(new LinuxRunner
                {
                    Name = "Wine Crossover 24",
                    Path = bin24,
                    IsProton = false,
                    IsRecommended = false,
                    NeedsDownload = false,
                    TargetFolderName = "Wine-Crossover-24"
                });
            }
            else
            {
                runners.Add(new LinuxRunner
                {
                    Name = "Wine Crossover 24",
                    Path = "DOWNLOAD_WINE_24",
                    IsProton = false,
                    IsRecommended = false,
                    NeedsDownload = true,
                    DownloadUrl = "https://github.com/Sikarugir-App/Engines/releases/download/v1.0/WS12WineCX24.0.7_7.tar.xz",
                    TargetFolderName = "Wine-Crossover-24"
                });
            }

            string localGptkDir = Path.Combine(runnersBase, "Game-Porting-Toolkit");
            string? binGptk = FindMacWineBinary(localGptkDir);
            if (string.IsNullOrEmpty(binGptk))
            {
                string legacyGptkDir = Path.Combine(installPath, "vers", "runner", "Game-Porting-Toolkit");
                binGptk = FindMacWineBinary(legacyGptkDir);
            }

            if (string.IsNullOrEmpty(binGptk))
            {
                string heroicGptkDir = Path.Combine(home, "Library", "Application Support", "heroic", "tools", "game-porting-toolkit", "Game-Porting-Toolkit-latest");
                binGptk = FindMacWineBinary(heroicGptkDir);
            }

            // Detect and purge obsolete GPTK 1.1 so the user gets updated to GPTK 3.0-3
            if (!string.IsNullOrEmpty(binGptk))
            {
                string gptk11Version = Path.Combine(localGptkDir, "wswine.bundle", "version");
                if (File.Exists(gptk11Version))
                {
                    try
                    {
                        string verText = File.ReadAllText(gptk11Version);
                        if (verText.Contains("v1."))
                        {
                            AppLogger.Log("Runner", "Detected obsolete GPTK 1.1. Purging so GPTK 3 can be installed...");
                            Directory.Delete(localGptkDir, true);
                            binGptk = null;
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Warning("Runner", "Could not purge obsolete GPTK 1.1 directory.", ex);
                        binGptk = null;
                    }
                }
            }

            if (!string.IsNullOrEmpty(binGptk))
            {
                runners.Add(new LinuxRunner
                {
                    Name = "GPTK 3 (Apple D3DMetal)",
                    Path = binGptk,
                    IsProton = false,
                    IsRecommended = false,
                    NeedsDownload = false,
                    TargetFolderName = "Game-Porting-Toolkit"
                });
            }
            else
            {
                runners.Add(new LinuxRunner
                {
                    Name = "GPTK 3 (Apple D3DMetal)",
                    Path = "DOWNLOAD_GPTK",
                    IsProton = false,
                    IsRecommended = false,
                    NeedsDownload = true,
                    DownloadUrl = "https://github.com/Gcenx/game-porting-toolkit/releases/download/Game-Porting-Toolkit-3.0-3/game-porting-toolkit-3.0-3.tar.xz",
                    TargetFolderName = "Game-Porting-Toolkit"
                });
            }

            string local23Dir = Path.Combine(runnersBase, "Wine-Crossover-23.7");
            string? bin23 = FindMacWineBinary(local23Dir);
            if (string.IsNullOrEmpty(bin23))
            {
                string legacy23Dir = Path.Combine(installPath, "vers", "runner", "Wine-Crossover-23.7");
                bin23 = FindMacWineBinary(legacy23Dir);
            }

            if (string.IsNullOrEmpty(bin23))
            {
                string heroicWineDir = Path.Combine(home, "Library", "Application Support", "heroic", "tools", "wine", "Wine-Crossover-latest");
                bin23 = FindMacWineBinary(heroicWineDir);
            }

            if (!string.IsNullOrEmpty(bin23))
            {
                runners.Add(new LinuxRunner
                {
                    Name = "Wine Crossover 23.7",
                    Path = bin23,
                    IsProton = false,
                    IsRecommended = false,
                    NeedsDownload = false,
                    NoteKey = "GoodForOldVersions",
                    TargetFolderName = "Wine-Crossover-23.7"
                });
            }
            else
            {
                runners.Add(new LinuxRunner
                {
                    Name = "Wine Crossover 23.7",
                    Path = "DOWNLOAD_WINE_23",
                    IsProton = false,
                    IsRecommended = false,
                    NeedsDownload = true,
                    NoteKey = "GoodForOldVersions",
                    DownloadUrl = "https://github.com/jfuruness/wine-crossover-mac/releases/download/23.7.1-1/wine-crossover-23.7.1-1-osx64.tar.xz",
                    ExpectedSha256 = "e24ba084737c8823e8439f7cb75d436a917fd92fc34b832bcaa0c0037eb33d03",
                    TargetFolderName = "Wine-Crossover-23.7"
                });
            }
        }

        public static string? FindMacWineBinary(string dir)
        {
            if (!Directory.Exists(dir)) return null;

            string[] directCandidates = {
                Path.Combine(dir, "Game Porting Toolkit.app", "Contents", "Resources", "wine", "bin", "wine"),
                Path.Combine(dir, "Game Porting Toolkit.app", "Contents", "Resources", "wine", "bin", "wine64"),
                Path.Combine(dir, "wswine.bundle", "bin", "wine"),
                Path.Combine(dir, "wswine.bundle", "bin", "wine64"),
                Path.Combine(dir, "Wine Crossover.app", "Contents", "Resources", "wine", "bin", "wine"),
                Path.Combine(dir, "Wine Crossover.app", "Contents", "Resources", "wine", "bin", "wine64"),
                Path.Combine(dir, "Contents", "Resources", "wine", "bin", "wine"),
                Path.Combine(dir, "Contents", "Resources", "wine", "bin", "wine64"),
                Path.Combine(dir, "Contents", "MacOS", "wine"),
                Path.Combine(dir, "bin", "wine"),
                Path.Combine(dir, "bin", "wine64")
            };

            foreach (var c in directCandidates)
            {
                if (File.Exists(c)) return c;
            }

            try
            {
                foreach (var sub in Directory.GetDirectories(dir))
                {
                    string name = Path.GetFileName(sub);
                    if (name.StartsWith(".") || name == "prefix") continue;
                    string? subFind = FindMacWineBinary(sub);
                    if (!string.IsNullOrEmpty(subFind)) return subFind;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warning("Runner", $"Could not scan macOS runner directory '{dir}'.", ex);
            }

            return null;
        }

        public static string? FindMacD3DMetalFramework(string? runnerRoot)
        {
            if (!OperatingSystem.IsMacOS()) return null;

            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string?[] candidates = {
                !string.IsNullOrEmpty(runnerRoot) ? Path.Combine(runnerRoot, "Game Porting Toolkit.app", "Contents", "Resources", "wine", "lib", "external", "D3DMetal.framework") : null,
                !string.IsNullOrEmpty(runnerRoot) ? Path.Combine(runnerRoot, "Contents", "Resources", "wine", "lib", "external", "D3DMetal.framework") : null,
                !string.IsNullOrEmpty(runnerRoot) ? Path.Combine(runnerRoot, "lib", "external", "D3DMetal.framework") : null,
                !string.IsNullOrEmpty(runnerRoot) ? Path.Combine(runnerRoot, "D3DMetal.framework") : null,
                "/usr/local/include/D3DMetal.framework",
                "/Library/Application Support/apple/game-porting-toolkit/D3DMetal.framework",
                !string.IsNullOrEmpty(home) ? Path.Combine(home, "Library", "Application Support", "heroic", "tools", "game-porting-toolkit", "D3DMetal.framework") : null,
                !string.IsNullOrEmpty(home) ? Path.Combine(home, "Library", "Application Support", "com.isaacmarovitz.Whisky", "Libraries", "D3DMetal.framework") : null
            };

            return candidates.FirstOrDefault(p => !string.IsNullOrEmpty(p) && Directory.Exists(p));
        }

        public static void CleanMacQuarantineAndSign(string dir)
        {
            if (!OperatingSystem.IsMacOS() || string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;

            try
            {
                // Remove all extended attributes recursively (-cr removes com.apple.quarantine, com.apple.provenance, etc.)
                Process.Start(new ProcessStartInfo
                {
                    FileName = "xattr",
                    ArgumentList = { "-cr", dir },
                    UseShellExecute = false,
                    CreateNoWindow = true
                })?.WaitForExit(10000);

                // Ensure executable permissions
                Process.Start(new ProcessStartInfo
                {
                    FileName = "chmod",
                    ArgumentList = { "-R", "+x", dir },
                    UseShellExecute = false,
                    CreateNoWindow = true
                })?.WaitForExit(5000);

                // Ad-hoc re-sign core binaries and dylibs locally so Gatekeeper trusts dlopen
                string[] filesToSign = {
                    Path.Combine(dir, "Game Porting Toolkit.app", "Contents", "Resources", "wine", "bin", "wine"),
                    Path.Combine(dir, "Game Porting Toolkit.app", "Contents", "Resources", "wine", "bin", "wineserver"),
                    Path.Combine(dir, "Game Porting Toolkit.app", "Contents", "Resources", "wine", "bin", "wine64"),
                    Path.Combine(dir, "Game Porting Toolkit.app", "Contents", "Resources", "wine", "bin", "wine-preloader"),
                    Path.Combine(dir, "Game Porting Toolkit.app"),
                    Path.Combine(dir, "wswine.bundle", "bin", "wine"),
                    Path.Combine(dir, "wswine.bundle", "bin", "wineserver"),
                    Path.Combine(dir, "wswine.bundle", "bin", "wine64"),
                    Path.Combine(dir, "wswine.bundle", "bin", "wine-preloader"),
                    Path.Combine(dir, "bin", "wine"),
                    Path.Combine(dir, "bin", "wineserver"),
                    Path.Combine(dir, "bin", "wine64"),
                    Path.Combine(dir, "wswine.bundle", "lib", "libcxfreetype.so"),
                    Path.Combine(dir, "wswine.bundle", "lib", "libfreetype.6.dylib"),
                    Path.Combine(dir, "wswine.bundle", "lib", "libfreetype.dylib"),
                    Path.Combine(dir, "wswine.bundle", "lib", "libMoltenVK.dylib"),
                    Path.Combine(dir, "wswine.bundle", "lib", "wine", "x86_64-unix", "libcxfreetype.so"),
                    Path.Combine(dir, "wswine.bundle", "lib", "wine", "x86_64-unix", "libfreetype.6.dylib"),
                    Path.Combine(dir, "wswine.bundle", "lib", "wine", "x86_64-unix", "libfreetype.dylib"),
                    Path.Combine(dir, "libcxfreetype.so"),
                    Path.Combine(dir, "libfreetype.6.dylib"),
                    Path.Combine(dir, "libfreetype.dylib"),
                    Path.Combine(dir, "libMoltenVK.dylib")
                };

                foreach (var file in filesToSign)
                {
                    if (File.Exists(file) || Directory.Exists(file))
                    {
                        try
                        {
                            Process.Start(new ProcessStartInfo
                            {
                                FileName = "codesign",
                                ArgumentList = { "--force", "-s", "-", file },
                                UseShellExecute = false,
                                CreateNoWindow = true
                            })?.WaitForExit(2000);
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warning("Runner", $"Could not clean quarantine or sign libraries in '{dir}'.", ex);
            }
        }

        public static bool DeployMacRuntime(string runnerDir, string? prefixPath = null, string? gameDir = null)
        {
            if (!OperatingSystem.IsMacOS()) return true;
            if (string.IsNullOrEmpty(runnerDir)) return false;

            try
            {
                string[] candidates = {
                    Path.Combine(AppContext.BaseDirectory, "Assets", "MacRuntime.tar.xz"),
                    Path.Combine(AppContext.BaseDirectory, "MacRuntime.tar.xz"),
                    Path.Combine(AppContext.BaseDirectory, "..", "Resources", "MacRuntime.tar.xz"),
                    Path.Combine(Directory.GetCurrentDirectory(), "Assets", "MacRuntime.tar.xz"),
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "MacRuntime.tar.xz")
                };

                string? archivePath = candidates.FirstOrDefault(File.Exists);

                if (archivePath == null)
                {
                    try
                    {
                        var uri = new Uri("avares://YSLauncher/Assets/MacRuntime.tar.xz");
                        if (Avalonia.Platform.AssetLoader.Exists(uri))
                        {
                            string tempArchive = Path.Combine(Path.GetTempPath(), "MacRuntime.tar.xz");
                            using (var stream = Avalonia.Platform.AssetLoader.Open(uri))
                            using (var fs = File.Create(tempArchive))
                            {
                                stream.CopyTo(fs);
                            }
                            archivePath = tempArchive;
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Warning("MacRuntime", "Could not export the bundled runtime archive.", ex);
                    }
                }

                string libInotify = Path.Combine(runnerDir, "libinotify.0.dylib");
                string libMoltenVK = Path.Combine(runnerDir, "libMoltenVK.dylib");
                string libFreeType = Path.Combine(runnerDir, "libfreetype.6.dylib");

                if ((!File.Exists(libInotify) || !File.Exists(libMoltenVK) || !File.Exists(libFreeType)) && archivePath != null)
                {
                    Directory.CreateDirectory(runnerDir);
                    var psi = new ProcessStartInfo
                    {
                        FileName = "tar",
                        ArgumentList = { "-xf", archivePath, "-C", runnerDir },
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using var p = Process.Start(psi);
                    if (p == null) throw new IOException("Could not start the runtime extractor.");
                    p.WaitForExit();
                    if (p.ExitCode != 0) throw new IOException($"Runtime extraction failed with exit code {p.ExitCode}.");
                }

                string[] wineLibSubdirs = {
                    Path.Combine(runnerDir, "Game Porting Toolkit.app", "Contents", "Resources", "wine", "lib"),
                    Path.Combine(runnerDir, "Game Porting Toolkit.app", "Contents", "Resources", "wine", "lib", "wine", "x86_64-unix"),
                    Path.Combine(runnerDir, "Contents", "Resources", "wine", "lib"),
                    Path.Combine(runnerDir, "Contents", "Resources", "wine", "lib", "wine", "x86_64-unix"),
                    Path.Combine(runnerDir, "wswine.bundle", "lib"),
                    Path.Combine(runnerDir, "wswine.bundle", "lib", "wine", "x86_64-unix"),
                    Path.Combine(runnerDir, "lib"),
                    Path.Combine(runnerDir, "lib", "wine", "x86_64-unix")
                };

                foreach (var sub in wineLibSubdirs)
                {
                    if (Directory.Exists(sub))
                    {
                        foreach (var ftFile in Directory.GetFiles(runnerDir, "lib*.*"))
                        {
                            string fname = Path.GetFileName(ftFile);
                            string dest = Path.Combine(sub, fname);
                            if (!File.Exists(dest))
                            {
                                try
                                {
                                    File.Copy(ftFile, dest, true);
                                }
                                catch (Exception ex)
                                {
                                    AppLogger.Warning("MacRuntime", $"Could not copy library '{ftFile}' to '{dest}'.", ex);
                                }
                            }
                        }

                        string cxfreetype = Path.Combine(sub, "libcxfreetype.so");
                        string ftTarget = Path.Combine(sub, "libfreetype.6.dylib");
                        if (!File.Exists(cxfreetype) && File.Exists(ftTarget))
                        {
                            try
                            {
                                Process.Start(new ProcessStartInfo
                                {
                                    FileName = "ln",
                                    ArgumentList = { "-sf", ftTarget, cxfreetype },
                                    UseShellExecute = false,
                                    CreateNoWindow = true
                                })?.WaitForExit();
                            }
                            catch (Exception ex)
                            {
                                AppLogger.Warning("MacRuntime", $"Could not create symlink '{cxfreetype}'.", ex);
                            }
                        }
                    }
                }

                string? d3dMetalFramework = FindMacD3DMetalFramework(runnerDir);
                bool isGptk = !string.IsNullOrEmpty(d3dMetalFramework) ||
                              runnerDir.Contains("game-porting-toolkit", StringComparison.OrdinalIgnoreCase) ||
                              runnerDir.Contains("gptk", StringComparison.OrdinalIgnoreCase);

                if (!isGptk && !string.IsNullOrEmpty(gameDir) && Directory.Exists(gameDir))
                {
                    string[] dxvkFiles = { "d3d11.dll", "d3d10core.dll", "dxvk.conf" };
                    foreach (var df in dxvkFiles)
                    {
                        string src = Path.Combine(runnerDir, df);
                        string dst = Path.Combine(gameDir, df);
                        if (File.Exists(src))
                        {
                            try
                            {
                                File.Copy(src, dst, true);
                            }
                            catch (Exception ex)
                            {
                                AppLogger.Warning("MacRuntime", $"Could not copy DXVK file '{src}' to '{dst}'.", ex);
                            }
                        }
                    }
                }

                if (!string.IsNullOrEmpty(prefixPath))
                {
                    string system32 = Path.Combine(prefixPath, "drive_c", "windows", "system32");
                    if (Directory.Exists(system32))
                    {
                        if (isGptk)
                        {
                            string[] gptkDlls = { "d3d12.dll", "d3d12core.dll", "dxgi.dll" };
                            string[] gptkCandidateDirs = {
                                Path.Combine(runnerDir, "redist", "lib"),
                                Path.Combine(runnerDir, "redist"),
                                Path.Combine(runnerDir, "lib", "wine", "x86_64-windows"),
                                Path.Combine(runnerDir, "Game Porting Toolkit.app", "Contents", "Resources", "wine", "lib", "wine", "x86_64-windows"),
                                Path.Combine(runnerDir, "Contents", "Resources", "wine", "lib", "wine", "x86_64-windows"),
                                Path.Combine(runnerDir, "lib", "external")
                            };

                            foreach (var cDir in gptkCandidateDirs)
                            {
                                if (Directory.Exists(cDir))
                                {
                                    foreach (var dll in gptkDlls)
                                    {
                                        string src = Path.Combine(cDir, dll);
                                        string dst = Path.Combine(system32, dll);
                                        if (File.Exists(src) && (!File.Exists(dst) || new FileInfo(dst).Length == 0))
                                        {
                                            try
                                            {
                                                File.Copy(src, dst, true);
                                                AppLogger.Log("MacRuntime", $"Installed GPTK library '{dll}' into system32.");
                                            }
                                            catch (Exception ex)
                                            {
                                                AppLogger.Warning("MacRuntime", $"Could not copy '{src}' to system32.", ex);
                                            }
                                        }
                                    }
                                }
                            }
                        }
                        else
                        {
                            string d3d11 = Path.Combine(system32, "d3d11.dll");
                            string d3d10core = Path.Combine(system32, "d3d10core.dll");

                            bool needsDxvk = !File.Exists(d3d11) || new FileInfo(d3d11).Length < 1000000;
                            if (needsDxvk)
                            {
                                string srcD3D11 = Path.Combine(runnerDir, "d3d11.dll");
                                string srcD3D10 = Path.Combine(runnerDir, "d3d10core.dll");

                                if (File.Exists(srcD3D11))
                                {
                                    try
                                    {
                                        File.Copy(srcD3D11, d3d11, true);
                                    }
                                    catch (Exception ex)
                                    {
                                        AppLogger.Warning("MacRuntime", $"Could not install '{srcD3D11}' into the Wine prefix.", ex);
                                    }
                                }
                                if (File.Exists(srcD3D10))
                                {
                                    try
                                    {
                                        File.Copy(srcD3D10, d3d10core, true);
                                    }
                                    catch (Exception ex)
                                    {
                                        AppLogger.Warning("MacRuntime", $"Could not install '{srcD3D10}' into the Wine prefix.", ex);
                                    }
                                }
                            }
                        }
                    }

                    string winDir = Path.Combine(prefixPath, "drive_c", "windows");
                    if (Directory.Exists(winDir))
                    {
                        string fontsDir = Path.Combine(winDir, "Fonts");
                        Directory.CreateDirectory(fontsDir);
                        string sysFonts = "/System/Library/Fonts/Supplemental";
                        if (Directory.Exists(sysFonts))
                        {
                            string[] fontNames = { "Arial", "Tahoma", "Times", "Verdana", "Comic", "Courier" };
                            foreach (var fn in fontNames)
                            {
                                foreach (var file in Directory.GetFiles(sysFonts, fn + "*.ttf"))
                                {
                                    string dest = Path.Combine(fontsDir, Path.GetFileName(file));
                                    if (!File.Exists(dest))
                                    {
                                        try
                                        {
                                            File.Copy(file, dest, true);
                                        }
                                        catch (Exception ex)
                                        {
                                            AppLogger.Warning("MacRuntime", $"Could not install font '{file}'.", ex);
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                DisableAudioCapturePrompt(runnerDir, prefixPath);
                CleanMacQuarantineAndSign(runnerDir);
                if (!string.IsNullOrEmpty(gameDir) && Directory.Exists(gameDir))
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = "xattr",
                            ArgumentList = { "-cr", gameDir },
                            UseShellExecute = false,
                            CreateNoWindow = true
                        })?.WaitForExit(3000);
                    }
                    catch { }
                }
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Error("MacRuntime", $"Could not deploy the macOS runtime to '{runnerDir}'.", ex);
                return false;
            }
        }

        public static void DisableAudioCapturePrompt(string runnerDir, string? prefixPath = null)
        {
            if (!OperatingSystem.IsMacOS() || string.IsNullOrEmpty(runnerDir) || !Directory.Exists(runnerDir)) return;

            try
            {
                var coreAudioFiles = Directory.GetFiles(runnerDir, "winecoreaudio.so", SearchOption.AllDirectories);
                foreach (var soPath in coreAudioFiles)
                {
                    try
                    {
                        byte[] data = File.ReadAllBytes(soPath);
                        bool modified = false;

                        byte[] origEp24 = new byte[] { 0x8b, 0x07, 0x83, 0xf8, 0x01, 0x74, 0x16, 0xb9, 0x57, 0x00, 0x07, 0x80, 0x85, 0xc0, 0x0f, 0x85, 0xe6, 0x01, 0x00, 0x00 };
                        byte[] newEp24  = new byte[] { 0x8b, 0x07, 0x85, 0xc0, 0x74, 0x0a, 0x31, 0xc9, 0xe9, 0xed, 0x01, 0x00, 0x00, 0x90, 0x90, 0x90, 0xeb, 0x02, 0x90, 0x90 };
                        int idxEp24 = IndexOfSequence(data, origEp24);
                        if (idxEp24 != -1)
                        {
                            Array.Copy(newEp24, 0, data, idxEp24, newEp24.Length);
                            modified = true;
                        }

                        byte[] origEp23 = new byte[] { 0xb9, 0x57, 0x00, 0x07, 0x80, 0x83, 0xf8, 0x01, 0x0f, 0x85, 0xee, 0x01, 0x00, 0x00, 0xb8, 0x20, 0x6e, 0x49, 0x64, 0xeb, 0x05 };
                        byte[] newEp23  = new byte[] { 0x31, 0xc9, 0xe9, 0xf5, 0x01, 0x00, 0x00, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90 };
                        int idxEp23 = IndexOfSequence(data, origEp23);
                        if (idxEp23 != -1)
                        {
                            Array.Copy(newEp23, 0, data, idxEp23, newEp23.Length);
                            modified = true;
                        }

                        byte[] p24 = new byte[] { 0x41, 0x83, 0x7d, 0x44, 0x01, 0x75, 0x6f };
                        int idx24 = IndexOfSequence(data, p24);
                        if (idx24 != -1)
                        {
                            data[idx24 + 5] = 0xeb;
                            modified = true;
                        }

                        byte[] p23 = new byte[] { 0x83, 0x7b, 0x44, 0x01, 0x75, 0x6d };
                        int idx23 = IndexOfSequence(data, p23);
                        if (idx23 != -1)
                        {
                            data[idx23 + 4] = 0xeb;
                            modified = true;
                        }

                        byte[] requestFuncHdr = new byte[] { 0x55, 0x48, 0x89, 0xe5 };
                        byte[] stubReq = new byte[] { 0x31, 0xc0, 0xc3, 0x90 };

                        if (data.Length > 0x3284 && MatchBytes(data, 0x3280, requestFuncHdr))
                        {
                            Array.Copy(stubReq, 0, data, 0x3280, stubReq.Length);
                            modified = true;
                        }
                        if (data.Length > 0xe4f4 && MatchBytes(data, 0xe4f0, requestFuncHdr))
                        {
                            Array.Copy(stubReq, 0, data, 0xe4f0, stubReq.Length);
                            modified = true;
                        }

                        byte[] stubDenied24 = new byte[] { 0xb8, 0x02, 0x00, 0x00, 0x00, 0xc3, 0x90 };
                        if (data.Length > 0x3217 && MatchBytes(data, 0x3210, requestFuncHdr))
                        {
                            Array.Copy(stubDenied24, 0, data, 0x3210, stubDenied24.Length);
                            modified = true;
                        }
                        byte[] stubDenied23 = new byte[] { 0xb8, 0x02, 0x00, 0x00, 0x00, 0xc3 };
                        byte[] origAuth23Hdr = new byte[] { 0x41, 0x57, 0x41, 0x56, 0x53, 0x50 };
                        if (data.Length > 0xe46a && MatchBytes(data, 0xe464, origAuth23Hdr))
                        {
                            Array.Copy(stubDenied23, 0, data, 0xe464, stubDenied23.Length);
                            modified = true;
                        }

                        byte[] guFlowCheck = new byte[] { 0x55, 0x48, 0x89, 0xe5, 0x85, 0xff, 0x74, 0x04, 0x31, 0xc0, 0x5d, 0xc3 };
                        byte[] origGuCommon = new byte[] { 0x55, 0x48, 0x89, 0xe5, 0x53, 0x48, 0x83, 0xec, 0x28, 0x89, 0xfb, 0x89 };
                        if (data.Length > 0x61fc && MatchBytes(data, 0x61f0, guFlowCheck))
                        {
                            Array.Copy(origGuCommon, 0, data, 0x61f0, origGuCommon.Length);
                            modified = true;
                        }
                        if (data.Length > 0xa2ac && MatchBytes(data, 0xa2a0, guFlowCheck))
                        {
                            Array.Copy(origGuCommon, 0, data, 0xa2a0, origGuCommon.Length);
                            modified = true;
                        }

                        byte[] origIoCheck24 = new byte[] { 0x83, 0xfb, 0x01, 0x0f, 0x85, 0xd2, 0x00, 0x00, 0x00 };
                        byte[] newIoCheck24  = new byte[] { 0x83, 0xfb, 0x01, 0xe9, 0xd3, 0x00, 0x00, 0x00, 0x90 };
                        int idxIo24 = IndexOfSequence(data, origIoCheck24);
                        if (idxIo24 != -1)
                        {
                            Array.Copy(newIoCheck24, 0, data, idxIo24, newIoCheck24.Length);
                            modified = true;
                        }

                        byte[] origIoCheck23 = new byte[] { 0x83, 0xfb, 0x01, 0x0f, 0x85, 0xaa, 0x00, 0x00, 0x00 };
                        byte[] newIoCheck23  = new byte[] { 0x83, 0xfb, 0x01, 0xe9, 0xab, 0x00, 0x00, 0x00, 0x90 };
                        int idxIo23 = IndexOfSequence(data, origIoCheck23);
                        if (idxIo23 != -1)
                        {
                            Array.Copy(newIoCheck23, 0, data, idxIo23, newIoCheck23.Length);
                            modified = true;
                        }

                        byte[] dOutPattern = new byte[] { 0x74, 0x75, 0x4f, 0x64 };
                        int idxDOut = IndexOfSequence(data, dOutPattern);
                        if (idxDOut != -1)
                        {
                            data[idxDOut + 3] = 0x73;
                            modified = true;
                        }

                        byte[] devHashPattern24 = new byte[] { 0xc7, 0x45, 0xc0, 0x23, 0x76, 0x65, 0x64 };
                        int idxDev24 = IndexOfSequence(data, devHashPattern24);
                        if (idxDev24 != -1 && idxDev24 == 0x35c1)
                        {
                            byte[] patch1 = new byte[0x35e2 - 0x35c1];
                            for (int i = 0; i < patch1.Length; i++) patch1[i] = 0x90;
                            byte[] p1Code = new byte[] { 0xc7, 0x45, 0x98, 0x04, 0x00, 0x00, 0x00, 0xe9, 0x77, 0x00, 0x00, 0x00 };
                            Array.Copy(p1Code, 0, patch1, 0, p1Code.Length);
                            Array.Copy(patch1, 0, data, 0x35c1, patch1.Length);

                            byte[] patch2 = new byte[0x36a6 - 0x3681];
                            for (int i = 0; i < patch2.Length; i++) patch2[i] = 0x90;
                            byte[] p2Code = new byte[] { 0x48, 0x89, 0x5d, 0xd0, 0x8b, 0x45, 0x94, 0x89, 0x03, 0xe9, 0xc4, 0x00, 0x00, 0x00 };
                            Array.Copy(p2Code, 0, patch2, 0, p2Code.Length);
                            Array.Copy(patch2, 0, data, 0x3681, patch2.Length);

                            modified = true;
                        }

                        byte[] devHashPattern23 = new byte[] { 0xc7, 0x45, 0xb8, 0x23, 0x76, 0x65, 0x64 };
                        int idxDev23 = IndexOfSequence(data, devHashPattern23);
                        if (idxDev23 != -1 && idxDev23 == 0x7b40)
                        {
                            byte[] patch1 = new byte[0x7b5f - 0x7b40];
                            for (int i = 0; i < patch1.Length; i++) patch1[i] = 0x90;
                            byte[] p1Code = new byte[] { 0xc7, 0x45, 0xb4, 0x04, 0x00, 0x00, 0x00, 0xe9, 0x7b, 0x00, 0x00, 0x00 };
                            Array.Copy(p1Code, 0, patch1, 0, p1Code.Length);
                            Array.Copy(patch1, 0, data, 0x7b40, patch1.Length);

                            byte[] patch2 = new byte[0x7c26 - 0x7c02];
                            for (int i = 0; i < patch2.Length; i++) patch2[i] = 0x90;
                            byte[] p2Code = new byte[] { 0x4d, 0x89, 0xfc, 0x8b, 0x45, 0xb0, 0x41, 0x89, 0x07, 0xe9, 0xcc, 0x00, 0x00, 0x00 };
                            Array.Copy(p2Code, 0, patch2, 0, p2Code.Length);
                            Array.Copy(patch2, 0, data, 0x7c02, patch2.Length);

                            modified = true;
                        }

                        if (modified)
                        {
                            File.WriteAllBytes(soPath, data);
                            Process.Start(new ProcessStartInfo
                            {
                                FileName = "/usr/bin/codesign",
                                ArgumentList = { "-f", "-s", "-", soPath },
                                UseShellExecute = false,
                                CreateNoWindow = true
                            })?.WaitForExit();
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Warning("Audio", $"Could not patch '{soPath}'.", ex);
                    }
                }

                if (!string.IsNullOrEmpty(prefixPath) && Directory.Exists(prefixPath))
                {
                    string userReg = Path.Combine(prefixPath, "user.reg");
                    if (File.Exists(userReg))
                    {
                        try
                        {
                            var lines = File.ReadAllLines(userReg);
                            var newLines = new List<string>();
                            bool skip = false;
                            bool changed = false;
                            foreach (var l in lines)
                            {
                                if (l.StartsWith("[") && l.Contains("winecoreaudio.drv") && (l.Contains(@"devices\1,") || l.Contains(@"devices\\1,") || l.Contains("EQM") || l.Contains("BlackHole") || l.Contains("Aggregate")))
                                {
                                    skip = true;
                                    changed = true;
                                    continue;
                                }
                                else if (l.StartsWith("[") && skip)
                                {
                                    skip = false;
                                }
                                if (!skip) newLines.Add(l);
                            }
                            if (changed)
                            {
                                File.WriteAllLines(userReg, newLines);
                            }
                        }
                        catch (Exception ex)
                        {
                            AppLogger.Warning("Audio", $"Could not update '{userReg}'.", ex);
                        }
                    }

                    string sysReg = Path.Combine(prefixPath, "system.reg");
                    if (File.Exists(sysReg))
                    {
                        try
                        {
                            var lines = File.ReadAllLines(sysReg);
                            var badGuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            string currentSection = "";
                            foreach (var l in lines)
                            {
                                if (l.StartsWith("["))
                                {
                                    currentSection = l;
                                    if (l.Contains("MMDevices") && l.Contains("Capture"))
                                    {
                                        var m = Regex.Match(l, @"\{[0-9a-fA-F-]{36}\}");
                                        if (m.Success) badGuids.Add(m.Value);
                                    }
                                }
                                else if (!string.IsNullOrEmpty(currentSection) && currentSection.Contains("MMDevices"))
                                {
                                    if (l.IndexOf("eqMac", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                        l.IndexOf("EQM", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                        l.IndexOf("BlackHole", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                        l.IndexOf("Aggregate", StringComparison.OrdinalIgnoreCase) >= 0)
                                    {
                                        var m = Regex.Match(currentSection, @"\{[0-9a-fA-F-]{36}\}");
                                        if (m.Success) badGuids.Add(m.Value);
                                    }
                                }
                            }

                            var newLines = new List<string>(lines.Length);
                            bool skip = false;
                            bool changed = false;
                            foreach (var l in lines)
                            {
                                if (l.StartsWith("["))
                                {
                                    if (l.Contains("MMDevices") && (l.Contains("Capture") || badGuids.Any(g => l.IndexOf(g, StringComparison.OrdinalIgnoreCase) >= 0)))
                                    {
                                        skip = true;
                                        changed = true;
                                        continue;
                                    }
                                    else if (skip)
                                    {
                                        skip = false;
                                    }
                                }
                                if (!skip) newLines.Add(l);
                            }
                            if (changed)
                            {
                                File.WriteAllLines(sysReg, newLines);
                            }
                        }
                        catch (Exception ex)
                        {
                            AppLogger.Warning("Audio", $"Could not update '{sysReg}'.", ex);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warning("Audio", $"Could not disable audio capture for '{runnerDir}'.", ex);
            }
        }

        private static int IndexOfSequence(byte[] buffer, byte[] pattern)
        {
            for (int i = 0; i <= buffer.Length - pattern.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < pattern.Length; j++)
                {
                    if (buffer[i + j] != pattern[j])
                    {
                        match = false;
                        break;
                    }
                }
                if (match) return i;
            }
            return -1;
        }

        private static bool MatchBytes(byte[] buffer, int offset, byte[] pattern)
        {
            if (offset + pattern.Length > buffer.Length) return false;
            for (int i = 0; i < pattern.Length; i++)
            {
                if (buffer[offset + i] != pattern[i]) return false;
            }
            return true;
        }

        public static string GetMacRunnerRoot(string runnerPath, string installPath, string targetFolderName = "")
        {
            if (!string.IsNullOrEmpty(targetFolderName))
            {
                string candidate = Path.Combine(installPath, "runner", targetFolderName);
                if (Directory.Exists(candidate)) return candidate;
                string legacyCandidate = Path.Combine(installPath, "vers", "runner", targetFolderName);
                if (Directory.Exists(legacyCandidate)) return legacyCandidate;
            }

            string runnersBaseDir = Path.Combine(installPath, "runner");
            string legacyRunnersBaseDir = Path.Combine(installPath, "vers", "runner");
            string cur = runnerPath;
            while (!string.IsNullOrEmpty(cur))
            {
                string? parent = Path.GetDirectoryName(cur);
                if (parent != null && (string.Equals(parent, runnersBaseDir, StringComparison.OrdinalIgnoreCase) ||
                                       string.Equals(parent, legacyRunnersBaseDir, StringComparison.OrdinalIgnoreCase)))
                {
                    return cur;
                }
                cur = parent ?? "";
            }

            string binDir = Path.GetDirectoryName(runnerPath) ?? "";
            string rDir = Path.GetDirectoryName(binDir) ?? "";
            string rParent = Path.GetDirectoryName(rDir) ?? "";
            return !string.IsNullOrEmpty(rParent) ? rParent : runnerPath;
        }

        public static List<string> GetMacLibraryPaths(string runnerPath, string installPath, string targetFolderName = "")
        {
            var paths = new HashSet<string>();
            string runnerRoot = GetMacRunnerRoot(runnerPath, installPath, targetFolderName);
            if (Directory.Exists(runnerRoot)) paths.Add(runnerRoot);

            string binDir = Path.GetDirectoryName(runnerPath) ?? "";
            string rDir = Path.GetDirectoryName(binDir) ?? "";
            string rParent = Path.GetDirectoryName(rDir) ?? "";

            if (!string.IsNullOrEmpty(rParent) && Directory.Exists(rParent)) paths.Add(rParent);
            if (!string.IsNullOrEmpty(rDir) && Directory.Exists(rDir)) paths.Add(rDir);
            if (!string.IsNullOrEmpty(binDir) && Directory.Exists(binDir)) paths.Add(binDir);

            string[] libSubdirs = {
                "lib",
                "wswine.bundle/lib",
                "Wine Crossover.app/Contents/Resources/wine/lib",
                "Game Porting Toolkit.app/Contents/Resources/wine/lib",
                "Contents/Resources/wine/lib",
                "lib/wine/x86_64-unix",
                "Game Porting Toolkit.app/Contents/Resources/wine/lib/wine/x86_64-unix"
            };

            var baseList = paths.ToList();
            foreach (var b in baseList)
            {
                foreach (var sub in libSubdirs)
                {
                    string p = Path.Combine(b, sub.Replace('/', Path.DirectorySeparatorChar));
                    if (Directory.Exists(p)) paths.Add(p);
                }
            }

            return paths.Where(Directory.Exists).Distinct().ToList();
        }

        public static bool IsRosettaInstalled()
        {
            if (!OperatingSystem.IsMacOS()) return true;
            if (RuntimeInformation.ProcessArchitecture != Architecture.Arm64 && RuntimeInformation.OSArchitecture != Architecture.Arm64)
            {
                return true;
            }

            if (File.Exists("/Library/Apple/usr/share/rosetta/rosetta"))
            {
                return true;
            }

            try
            {
                using var p = Process.Start(new ProcessStartInfo
                {
                    FileName = "/usr/bin/arch",
                    ArgumentList = { "-x86_64", "/usr/bin/true" },
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                if (p == null) return false;
                p.WaitForExit();
                return p.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }

        public static async Task<bool> InstallRosettaAsync()
        {
            if (IsRosettaInstalled()) return true;

            try
            {
                AppLogger.Log("Rosetta", "Starting Rosetta 2 installation via softwareupdate...");
                using var p = Process.Start(new ProcessStartInfo
                {
                    FileName = "/usr/sbin/softwareupdate",
                    ArgumentList = { "--install-rosetta", "--agree-to-license" },
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                if (p == null) return false;
                await p.WaitForExitAsync();
                AppLogger.Log("Rosetta", $"softwareupdate exited with code {p.ExitCode}");
                return p.ExitCode == 0 || IsRosettaInstalled();
            }
            catch (Exception ex)
            {
                AppLogger.Error("Rosetta", "Failed to install Rosetta 2", ex);
                return false;
            }
        }
    }
