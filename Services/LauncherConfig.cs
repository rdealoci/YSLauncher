using System;
using System.IO;
using System.Text.Json;

namespace YSLauncher.Services;

    public class LauncherConfigModel
    {
        public string? Language { get; set; }
        public bool TutorialCompleted { get; set; }
        public string? InstallPath { get; set; }
        public bool AutoUpdate { get; set; }
        public bool CloseOnLaunch { get; set; }
        public int CloseOnLaunchMode { get; set; }
        public string? SelectedRunner { get; set; }
        public string? LastCheckTime { get; set; }
        public string? LastNotifiedBuild { get; set; }
        public string? LatestPostUrl { get; set; }
        public bool HasPendingUpdate { get; set; }
    }

    public static class LauncherConfig
    {
        private static readonly object _lock = new();
        private static LauncherConfigModel _current = new();
        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        public static string ConfigDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "YSLauncherConfig"
        );

        public static string ConfigFile => Path.Combine(ConfigDir, "config.json");
        public static string LogFile => Path.Combine(ConfigDir, "launcher.log");
        public static string LogPreviousFile => Path.Combine(ConfigDir, "launcher.previous.log");
        public static string ArchiveVersionsCacheFile => Path.Combine(ConfigDir, "archive_versions_cache.txt");
        public static string CustomModeBackupFile => Path.Combine(ConfigDir, "cmbkp.zip");
        public static string CustomModeBackupBakFile => Path.Combine(ConfigDir, "cmbkp.zip.bak");
        public static string LogoPngFile => Path.Combine(ConfigDir, "logo.png");
        public static string IconIcoFile => Path.Combine(ConfigDir, "icon.ico");
        public static string FlagsDir => Path.Combine(ConfigDir, "flags");
        public static string PoseModBackupDir => Path.Combine(ConfigDir, "pm_backup");
        public static bool HasPoseModBackup => Directory.Exists(PoseModBackupDir);

        static LauncherConfig()
        {
            Load();
        }

        public static void Reload() => Load();

        public static void Load()
        {
            lock (_lock)
            {
                try
                {
                    if (File.Exists(ConfigFile))
                    {
                        string json = File.ReadAllText(ConfigFile);
                        var loaded = JsonSerializer.Deserialize<LauncherConfigModel>(json, _jsonOptions);
                        if (loaded != null)
                        {
                            _current = loaded;
                            if (_current.CloseOnLaunchMode == 0 && _current.CloseOnLaunch)
                            {
                                _current.CloseOnLaunchMode = 1;
                            }
                            CleanLegacyFilesIfPresent();
                            return;
                        }
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Warning("Config", "Failed to deserialize config.json, attempting fallback or migration.", ex);
                }

                MigrateLegacyFilesIfPresent();
            }
        }

        private static void MigrateLegacyFilesIfPresent()
        {
            try
            {
                bool migratedAny = false;

                string legacyLang = Path.Combine(ConfigDir, "language_configured.txt");
                if (File.Exists(legacyLang))
                {
                    string val = File.ReadAllText(legacyLang).Trim();
                    if (!string.IsNullOrEmpty(val)) { _current.Language = val; migratedAny = true; }
                }

                string legacyTut = Path.Combine(ConfigDir, "tutorial_completed.txt");
                if (File.Exists(legacyTut))
                {
                    _current.TutorialCompleted = true;
                    migratedAny = true;
                }

                string legacyInstall = Path.Combine(ConfigDir, "install_path.txt");
                if (File.Exists(legacyInstall))
                {
                    string val = File.ReadAllText(legacyInstall).Trim();
                    if (!string.IsNullOrEmpty(val)) { _current.InstallPath = val; migratedAny = true; }
                }

                string legacyAuto = Path.Combine(ConfigDir, "auto_update.txt");
                if (File.Exists(legacyAuto))
                {
                    _current.AutoUpdate = File.ReadAllText(legacyAuto).Trim() == "1";
                    migratedAny = true;
                }

                string legacyClose = Path.Combine(ConfigDir, "close_on_launch.txt");
                if (File.Exists(legacyClose))
                {
                    _current.CloseOnLaunch = File.ReadAllText(legacyClose).Trim() == "1";
                    migratedAny = true;
                }

                string legacyRunner = Path.Combine(ConfigDir, "selected_runner.txt");
                if (File.Exists(legacyRunner))
                {
                    string val = File.ReadAllText(legacyRunner).Trim();
                    if (!string.IsNullOrEmpty(val)) { _current.SelectedRunner = val; migratedAny = true; }
                }

                string legacyLastCheck = Path.Combine(ConfigDir, "last_check_time.txt");
                if (File.Exists(legacyLastCheck))
                {
                    string val = File.ReadAllText(legacyLastCheck).Trim();
                    if (!string.IsNullOrEmpty(val)) { _current.LastCheckTime = val; migratedAny = true; }
                }

                string legacyLastNotified = Path.Combine(ConfigDir, "last_notified_build.txt");
                if (File.Exists(legacyLastNotified))
                {
                    string val = File.ReadAllText(legacyLastNotified).Trim();
                    if (!string.IsNullOrEmpty(val)) { _current.LastNotifiedBuild = val; migratedAny = true; }
                }

                string legacyLatestPost = Path.Combine(ConfigDir, "latest_post_url.txt");
                if (File.Exists(legacyLatestPost))
                {
                    string val = File.ReadAllText(legacyLatestPost).Trim();
                    if (!string.IsNullOrEmpty(val)) { _current.LatestPostUrl = val; migratedAny = true; }
                }

                string legacyPending = Path.Combine(ConfigDir, "pending_update.txt");
                if (File.Exists(legacyPending))
                {
                    _current.HasPendingUpdate = true;
                    migratedAny = true;
                }

                if (migratedAny)
                {
                    SaveInternal();
                    CleanLegacyFilesIfPresent();
                    AppLogger.Log("Config", "Successfully migrated legacy configuration files into config.json.");
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warning("Config", "Error during migration of legacy configuration files.", ex);
            }
        }

        private static void CleanLegacyFilesIfPresent()
        {
            string[] legacyFiles = new[]
            {
                Path.Combine(ConfigDir, "language_configured.txt"),
                Path.Combine(ConfigDir, "tutorial_completed.txt"),
                Path.Combine(ConfigDir, "install_path.txt"),
                Path.Combine(ConfigDir, "auto_update.txt"),
                Path.Combine(ConfigDir, "close_on_launch.txt"),
                Path.Combine(ConfigDir, "selected_runner.txt"),
                Path.Combine(ConfigDir, "last_check_time.txt"),
                Path.Combine(ConfigDir, "last_notified_build.txt"),
                Path.Combine(ConfigDir, "latest_post_url.txt"),
                Path.Combine(ConfigDir, "pending_update.txt")
            };

            foreach (var file in legacyFiles)
            {
                try
                {
                    if (File.Exists(file))
                    {
                        File.Delete(file);
                    }
                }
                catch { }
            }
        }

        private static void SaveInternal()
        {
            lock (_lock)
            {
                try
                {
                    if (!Directory.Exists(ConfigDir))
                    {
                        Directory.CreateDirectory(ConfigDir);
                    }

                    string json = JsonSerializer.Serialize(_current, _jsonOptions);
                    string tempFile = ConfigFile + ".tmp";
                    File.WriteAllText(tempFile, json);
                    File.Move(tempFile, ConfigFile, true);
                }
                catch (Exception ex)
                {
                    AppLogger.Warning("Config", "Failed to write config.json", ex);
                }
            }
        }

        public static string? Language
        {
            get { lock (_lock) return _current.Language; }
            set
            {
                lock (_lock)
                {
                    if (_current.Language != value)
                    {
                        _current.Language = value;
                        SaveInternal();
                    }
                }
            }
        }

        public static bool HasConfiguredLanguage => !string.IsNullOrWhiteSpace(Language);

        public static bool TutorialCompleted
        {
            get { lock (_lock) return _current.TutorialCompleted; }
            set
            {
                lock (_lock)
                {
                    if (_current.TutorialCompleted != value)
                    {
                        _current.TutorialCompleted = value;
                        SaveInternal();
                    }
                }
            }
        }

        public static string? InstallPath
        {
            get { lock (_lock) return _current.InstallPath; }
            set
            {
                lock (_lock)
                {
                    if (_current.InstallPath != value)
                    {
                        _current.InstallPath = value;
                        SaveInternal();
                    }
                }
            }
        }

        public static bool AutoUpdate
        {
            get { lock (_lock) return _current.AutoUpdate; }
            set
            {
                lock (_lock)
                {
                    if (_current.AutoUpdate != value)
                    {
                        _current.AutoUpdate = value;
                        SaveInternal();
                    }
                }
            }
        }

        public static bool CloseOnLaunch
        {
            get { lock (_lock) return _current.CloseOnLaunch; }
            set
            {
                lock (_lock)
                {
                    if (_current.CloseOnLaunch != value)
                    {
                        _current.CloseOnLaunch = value;
                        if (!value) _current.CloseOnLaunchMode = 0;
                        else if (_current.CloseOnLaunchMode == 0) _current.CloseOnLaunchMode = 1;
                        SaveInternal();
                    }
                }
            }
        }

        public static int CloseOnLaunchMode
        {
            get { lock (_lock) return _current.CloseOnLaunchMode; }
            set
            {
                lock (_lock)
                {
                    if (_current.CloseOnLaunchMode != value)
                    {
                        _current.CloseOnLaunchMode = value;
                        _current.CloseOnLaunch = value > 0;
                        SaveInternal();
                    }
                }
            }
        }

        public static string? SelectedRunner
        {
            get { lock (_lock) return _current.SelectedRunner; }
            set
            {
                lock (_lock)
                {
                    if (_current.SelectedRunner != value)
                    {
                        _current.SelectedRunner = value;
                        SaveInternal();
                    }
                }
            }
        }

        public static string? LastCheckTime
        {
            get { lock (_lock) return _current.LastCheckTime; }
            set
            {
                lock (_lock)
                {
                    if (_current.LastCheckTime != value)
                    {
                        _current.LastCheckTime = value;
                        SaveInternal();
                    }
                }
            }
        }

        public static string? LastNotifiedBuild
        {
            get { lock (_lock) return _current.LastNotifiedBuild; }
            set
            {
                lock (_lock)
                {
                    if (_current.LastNotifiedBuild != value)
                    {
                        _current.LastNotifiedBuild = value;
                        SaveInternal();
                    }
                }
            }
        }

        public static string? LatestPostUrl
        {
            get { lock (_lock) return _current.LatestPostUrl; }
            set
            {
                lock (_lock)
                {
                    if (_current.LatestPostUrl != value)
                    {
                        _current.LatestPostUrl = value;
                        SaveInternal();
                    }
                }
            }
        }

        public static bool HasPendingUpdate
        {
            get { lock (_lock) return _current.HasPendingUpdate; }
            set
            {
                lock (_lock)
                {
                    if (_current.HasPendingUpdate != value)
                    {
                        _current.HasPendingUpdate = value;
                        SaveInternal();
                    }
                }
            }
        }

        public static void ConsumePendingUpdate()
        {
            lock (_lock)
            {
                if (_current.HasPendingUpdate)
                {
                    _current.HasPendingUpdate = false;
                    SaveInternal();
                }
            }
        }
    }
