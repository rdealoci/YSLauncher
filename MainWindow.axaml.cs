using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.Layout;
using YSLauncher.Services;
using YSLauncher.Platforms.Linux;

namespace YSLauncher
{
    public partial class MainWindow : Window
    {
        private GameUpdater _updater;
        private string _installPath;
        private int _closeOnLaunchMode = 0;
        private bool _closeOnLaunch => _closeOnLaunchMode > 0;
        private bool _autoUpdate = false;
        private bool _isPlaying = false;
        private GameProcessSession? _runningGameProcess = null;
        private bool _isPoseModShiftHeld = false;
        private int _operationDepth;
        private bool _isStopping;
        private CancellationTokenSource? _downloadCancellation;
        private string? _operationError;
        private Task _backgroundUpdateTask = Task.CompletedTask;
        private CancellationTokenSource? _backgroundUpdateCancellation;
        private bool _isUpdatingTextProgrammatically = false;
        private bool _isCheckingUpdates = false;
        private bool _hasUpdateAvailable = false;
        private string _selectedVersion = "Latest";
        private bool _isInitialized = false;
        private string? _pendingInitialVersion;
        private List<string> _catalogVersions = new();
        private DispatcherTimer? _pulseTimer;
        private double _pulsePhase = 0;
        private ScaleTransform? _logoScale;
        private DispatcherTimer? _loadingHeartTimer;
        private int _loadingHeartFrame = 0;
        private ScaleTransform? _loadingHeartScale;
        private RotateTransform? _loadingHeartRotate;
        private TransformGroup? _loadingTransformGroup;
        private bool _isSyncingCustomMode = false;
        private string _detectedLanguageCode = "en";
        private string? _lastLoggedState;

        public static MainWindow? Instance { get; private set; }
        public string CurrentInstallPath => _installPath;
        public string CurrentSelectedVersion => _selectedVersion;
        public LinuxRunner? CurrentRunner => LinuxRunnerComboBox.SelectedItem as LinuxRunner;
        public GameUpdater Updater => _updater;

        public MainWindow()
        {
            Instance = this;
            InitializeComponent();
            _updater = new GameUpdater();
            _updater.StatusChanged += Updater_StatusChanged;
            _updater.ProgressChanged += Updater_ProgressChanged;
            _updater.DownloadCompleted += Updater_DownloadCompleted;

            string configDir = LauncherConfig.ConfigDir;
            if (!Directory.Exists(configDir))
            {
                Directory.CreateDirectory(configDir);
            }

            if (OperatingSystem.IsMacOS())
            {
                _ = Task.Run(() =>
                {
                    try
                    {
                        string baseDir = AppContext.BaseDirectory;
                        int idx = baseDir.IndexOf(".app/Contents/", StringComparison.OrdinalIgnoreCase);
                        if (idx > 0)
                        {
                            string appBundle = baseDir.Substring(0, idx + 4);
                            if (Directory.Exists(appBundle))
                            {
                                Process.Start(new ProcessStartInfo
                                {
                                    FileName = "xattr",
                                    ArgumentList = { "-cr", appBundle },
                                    UseShellExecute = false,
                                    CreateNoWindow = true
                                })?.WaitForExit(3000);
                            }
                        }
                    }
                    catch { }
                });
            }

            _closeOnLaunchMode = LauncherConfig.CloseOnLaunchMode;
            if (_closeOnLaunchMode > 1) _closeOnLaunchMode = 1;
            UpdateCloseOnLaunchUI();

            _autoUpdate = LauncherConfig.AutoUpdate;
            UpdateAutoUpdateUI();

            if (_autoUpdate && I18n.HasConfiguredLanguage)
            {
                _ = Task.Run(() => UpdateNotificationService.RegisterScheduler());
            }

            string? savedInstallPath = LauncherConfig.InstallPath;
            if (!string.IsNullOrEmpty(savedInstallPath))
            {
                _installPath = savedInstallPath;
                if ((_installPath.Equals(@"C:\Games", StringComparison.OrdinalIgnoreCase) ||
                     _installPath.Equals(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Games"), StringComparison.OrdinalIgnoreCase))
                    && !_updater.IsGameInstalled(_installPath, "Latest"))
                {
                    _installPath = OperatingSystem.IsWindows() 
                        ? @"C:\Yansim" 
                        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Yansim");
                    LauncherConfig.InstallPath = _installPath;
                }
            }
            else
            {
                if (OperatingSystem.IsWindows())
                {
                    _installPath = @"C:\Yansim";
                }
                else
                {
                    _installPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Yansim");
                }
                LauncherConfig.InstallPath = _installPath;
            }

            string normalizedStartup = _updater.NormalizeInstallPath(_installPath, out string? startupDetectedVersion);
            if (!string.Equals(normalizedStartup, _installPath, StringComparison.OrdinalIgnoreCase))
            {
                _installPath = normalizedStartup;
                LauncherConfig.InstallPath = _installPath;
            }
            if (!string.IsNullOrEmpty(startupDetectedVersion))
            {
                _pendingInitialVersion = startupDetectedVersion;
            }

            _isUpdatingTextProgrammatically = true;
            PathTextBox.Text = _installPath;
            _isUpdatingTextProgrammatically = false;

            _updater.MigrateOldInstall(_installPath);

            I18n.LanguageChanged += UpdateTexts;
            I18n.AutoDetect();
            _ = LoadAllFlagsAsync();
            _ = Task.Run(async () => await UpdateNotificationService.FetchLatestPostUrlAsync());

            if (!I18n.HasConfiguredLanguage)
            {
                ShowLanguageScreen(isInitial: true);
            }
            else
            {
                ShowMainScreen();
            }

            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                LinuxRunnerContainer.IsVisible = true;
                LinuxRunnerComboBox.SelectionChanged += LinuxRunnerComboBox_SelectionChanged;
                InitializeLinuxRunners();
            }

            if (OperatingSystem.IsMacOS() && (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 || RuntimeInformation.OSArchitecture == Architecture.Arm64))
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        if (!LinuxEngine.IsRosettaInstalled())
                        {
                            AppLogger.Log("Rosetta", "Rosetta 2 not detected on startup. Starting background install...");
                            await LinuxEngine.InstallRosettaAsync();
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Warning("Rosetta", "Background Rosetta check encountered an error.", ex);
                    }
                });
            }


            if (_selectedVersion == "Latest")
            {
                _isCheckingUpdates = true;
            }
            AddHandler(KeyDownEvent, Window_PreviewKeyDown, RoutingStrategies.Tunnel);
            AddHandler(KeyUpEvent, Window_PreviewKeyUp, RoutingStrategies.Tunnel);
            Deactivated += (s, e) => SetPoseModShiftState(false);
            LostFocus += (s, e) => SetPoseModShiftState(false);
            PlayButton.PointerEntered += (s, e) =>
            {
                if (_isPoseModShiftHeld)
                {
                    Avalonia.Controls.ToolTip.SetIsOpen(PlayButton, true);
                }
            };
            PlayButton.PointerExited += (s, e) =>
            {
                if (_isPoseModShiftHeld)
                {
                    Avalonia.Controls.ToolTip.SetIsOpen(PlayButton, false);
                }
            };
            CheckInstallState();
            _isInitialized = true;
            AppLogger.Log("Init", $"MainWindow initialized. InstallPath='{_installPath}', Language='{I18n.CurrentLang}', CloseOnLaunch={_closeOnLaunch}");
            _ = InitializeStartupAsync();
        }

        private void MinimizeButton_Click(object? sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void LinuxRunnerComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (LinuxRunnerComboBox.SelectedItem is LinuxRunner runner)
            {
                if (!string.IsNullOrEmpty(runner.Path) && runner.Path != "DOWNLOAD_GE")
                {
                    LauncherConfig.SelectedRunner = runner.Path;
                    AppLogger.Log("Runner", $"Saved selected runner: '{runner.Name}' ({runner.Path})");
                }
            }
        }

        private void InitializeLinuxRunners(string? preferredPath = null)
        {
            var prevSelected = LinuxRunnerComboBox.SelectedItem as LinuxRunner;
            var runners = LinuxEngine.ScanRunners(_installPath);
            LinuxRunnerComboBox.ItemsSource = runners;
            if (!runners.Any()) return;

            string? targetPath = preferredPath ?? prevSelected?.Path;

            if (string.IsNullOrEmpty(targetPath))
            {
                targetPath = LauncherConfig.SelectedRunner;
            }

            LinuxRunner? targetRunner = null;
            if (!string.IsNullOrEmpty(targetPath))
            {
                targetRunner = runners.FirstOrDefault(r => string.Equals(r.Path, targetPath, StringComparison.OrdinalIgnoreCase))
                            ?? runners.FirstOrDefault(r => string.Equals(r.Name, targetPath, StringComparison.OrdinalIgnoreCase))
                            ?? (targetPath.Contains("GPTK") ? runners.FirstOrDefault(r => r.Name.Contains("GPTK")) : null);
            }

            if (targetRunner == null)
            {
                targetRunner = runners.FirstOrDefault(r => !r.NeedsDownload) ?? runners.FirstOrDefault();
            }

            if (targetRunner != null)
            {
                LinuxRunnerComboBox.SelectedItem = targetRunner;
            }
        }

        private async Task InitializeVersionsAsync()
        {
            AppLogger.Log("Catalog", "Fetching Archive.org versions catalog...");
            var versions = await _updater.FetchArchiveVersionsAsync();
            _catalogVersions = versions;
            AppLogger.Log("Catalog", $"Fetched {versions.Count} catalog versions.");

            string path = _installPath;
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            {
                await Task.Run(() =>
                {
                    _updater.GetInstalledVersionsMap(path);
                });
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                PopulateVersionComboBox(_pendingInitialVersion ?? _selectedVersion);
                AppLogger.Log("Catalog", $"Catalog population complete. Total items in ComboBox: {VersionComboBox.Items.Count}");
            });
        }

        private async Task InitializeStartupAsync()
        {
            ShowLoadingOverlay(true);

            Task? startupTask = null;
            try
            {
                startupTask = RunStartupTasksAsync();
                await Task.WhenAny(startupTask, Task.Delay(5000));
            }
            catch (Exception ex)
            {
                AppLogger.Error("Startup", "Launcher initialization failed.", ex);
            }
            finally
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    ShowLoadingOverlay(false);
                });
            }

            if (Program.AutoUpdateRequested)
            {
                Program.AutoUpdateRequested = false;
                AppLogger.Log("AutoUpdate", "Auto-update requested via launch argument/notification. Triggering update...");
                if (startupTask != null)
                {
                    try { await startupTask; } catch { }
                }
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    SelectVersionInComboBox("Latest");
                    CheckInstallState();
                    UpdateButton_Click(null, new RoutedEventArgs());
                });
            }
        }

        private async Task RunStartupTasksAsync()
        {
            try
            {
                await Task.WhenAll(InitializeVersionsAsync(), PerformBackgroundUpdateCheckAsync());
            }
            catch (Exception ex)
            {
                AppLogger.Warning("Startup", "One or more startup tasks failed.", ex);
            }
        }

        private string FormatVersionName(string raw)
        {
            string cleanRaw = Path.GetFileNameWithoutExtension(raw).Replace(".rz", "");
            
            var (year, month, day) = GameUpdater.ParseDateFromVersion(raw);
            if (year > 0 && month > 0 && day > 0)
            {
                try
                {
                    DateTime dt = new DateTime(year, month, day);
                    string prefix = "Version";
                    System.Globalization.CultureInfo culture = new System.Globalization.CultureInfo("en-US");

                    switch (I18n.CurrentLang)
                    {
                        case "pt": prefix = "Versão"; culture = new System.Globalization.CultureInfo("pt-BR"); break;
                        case "es": prefix = "Versión"; culture = new System.Globalization.CultureInfo("es-ES"); break;
                        case "it": prefix = "Versione"; culture = new System.Globalization.CultureInfo("it-IT"); break;
                        case "ru": prefix = "Версия"; culture = new System.Globalization.CultureInfo("ru-RU"); break;
                        case "fr": prefix = "Version"; culture = new System.Globalization.CultureInfo("fr-FR"); break;
                        case "de": prefix = "Version"; culture = new System.Globalization.CultureInfo("de-DE"); break;
                        case "ja": prefix = "バージョン"; culture = new System.Globalization.CultureInfo("ja-JP"); break;
                        case "ko": prefix = "버전"; culture = new System.Globalization.CultureInfo("ko-KR"); break;
                        case "zh": prefix = "版本"; culture = new System.Globalization.CultureInfo("zh-CN"); break;
                        case "tr": prefix = "Sürüm"; culture = new System.Globalization.CultureInfo("tr-TR"); break;
                        case "th": prefix = "เวอร์ชัน"; culture = new System.Globalization.CultureInfo("th-TH"); break;
                        case "id": prefix = "Versi"; culture = new System.Globalization.CultureInfo("id-ID"); break;
                        case "pl": prefix = "Wersja"; culture = new System.Globalization.CultureInfo("pl-PL"); break;
                    }

                    string extra = "";
                    var mExtra = System.Text.RegularExpressions.Regex.Match(cleanRaw, @"-\s*([A-Za-z])(?:\s|$)");
                    if (mExtra.Success)
                    {
                        extra = $" ({mExtra.Groups[1].Value.ToUpper()})";
                    }

                    return $"{prefix} {dt.ToString("d", culture)}{extra}";
                }
                catch (Exception ex)
                {
                    AppLogger.Warning("Versions", $"Could not format catalog version '{raw}'.", ex);
                }
            }

            if (cleanRaw.Contains("Prototype")) return "Prototype (2014)";
            if (cleanRaw.Contains("Benchmarking")) return "Benchmarking Test (2014)";
            if (cleanRaw.Contains("SchoolTest")) return "School Test (2014)";
            if (cleanRaw.Contains("FinalRelease")) return "Final Release (Demo)";

            return cleanRaw;
        }

        private void PopulateVersionComboBox(string? versionToSelect = null)
        {
            if (!Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.Post(() => PopulateVersionComboBox(versionToSelect));
                return;
            }

            _isUpdatingTextProgrammatically = true;
            string targetSelection = versionToSelect ?? _selectedVersion ?? "Latest";

            var installedMap = _updater.GetInstalledVersionsMap(_installPath);
            bool isLowEnd = LinuxEngine.IsLowEndPc();
            bool isLatestInstalled = installedMap.ContainsKey("Latest");
            string? latestDir = installedMap.TryGetValue("Latest", out var ld) ? ld : null;

            var installedList = new List<string>();
            var archiveList = new List<string>();

            if (_catalogVersions != null && _catalogVersions.Count > 0)
            {
                foreach (var v in _catalogVersions)
                {
                    if (_updater.MatchesInstalledMap(v, installedMap))
                    {
                        string? matchedDir = _updater.GetGameDirectory(_installPath, v, installedMap);
                        if (isLatestInstalled && latestDir != null && string.Equals(matchedDir, latestDir, StringComparison.OrdinalIgnoreCase))
                        {
                            // The latest version is already installed and represented at the top; avoid duplicates.
                        }
                        else
                        {
                            installedList.Add(v);
                        }
                    }
                    else
                    {
                        archiveList.Add(v);
                    }
                }
            }

            // Identify locally installed versions on disk not already represented in installedList
            var distinctDirs = installedMap.Values.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            foreach (var dir in distinctDirs)
            {
                // The directory used for Latest is already represented by the "Latest" option
                if (latestDir != null && string.Equals(dir, latestDir, StringComparison.OrdinalIgnoreCase))
                    continue;

                // Check if any version in installedList already maps to this directory
                bool alreadyInList = false;
                foreach (var inv in installedList)
                {
                    string? invDir = _updater.GetGameDirectory(_installPath, inv, installedMap);
                    if (invDir != null && string.Equals(invDir, dir, StringComparison.OrdinalIgnoreCase))
                    {
                        alreadyInList = true;
                        break;
                    }
                }
                if (alreadyInList) continue;

                var bDate = _updater.GetLocalBuildDateFromDirectory(dir);
                if (bDate == null)
                {
                    var (py, pm, pd) = GameUpdater.ParseDateFromVersion(Path.GetFileName(dir));
                    if (py > 0 && pm > 0 && pd > 0)
                    {
                        bDate = new DateTime(py, pm, pd);
                    }
                }

                if (bDate != null)
                {
                    if (bDate.Value.Year >= 2024)
                    {
                        if (!isLatestInstalled)
                        {
                            isLatestInstalled = true;
                            installedMap["Latest"] = dir;
                        }
                    }
                    else
                    {
                        string? catalogMatch = _catalogVersions != null
                            ? _updater.FindVersionInCatalog(_catalogVersions, Path.GetFileName(dir), _installPath)
                            : null;

                        if (catalogMatch != null)
                        {
                            archiveList.Remove(catalogMatch);
                            if (!installedList.Contains(catalogMatch, StringComparer.OrdinalIgnoreCase))
                            {
                                installedList.Add(catalogMatch);
                            }
                        }
                        else
                        {
                            string standardTag = $"ys_{bDate.Value.Year}_{bDate.Value.Month:D2}_{bDate.Value.Day:D2}";
                            if (!installedList.Contains(standardTag, StringComparer.OrdinalIgnoreCase))
                            {
                                installedList.Add(standardTag);
                            }
                        }
                    }
                }
            }

            VersionComboBox.Items.Clear();

            // Installed versions section (prioritized at the top if present)
            bool hasAnyInstalled = isLatestInstalled || installedList.Count > 0;
            if (hasAnyInstalled)
            {
                VersionComboBox.Items.Add(new ComboBoxItem
                {
                    Content = $"── {I18n.T("HeaderInstalled")} ──",
                    Tag = "__HEADER__",
                    IsEnabled = false,
                    Focusable = false,
                    FontSize = 13,
                    FontWeight = FontWeight.Bold,
                    Foreground = new SolidColorBrush(Color.Parse("#E88CA6")),
                    Margin = new Thickness(0, 4, 0, 2)
                });

                if (isLatestInstalled)
                {
                    string text = I18n.T("LatestVersion");
                    if (!_hasUpdateAvailable) text += I18n.T("Installed");
                    VersionComboBox.Items.Add(new ComboBoxItem
                    {
                        Content = text,
                        Tag = "Latest"
                    });
                }

                foreach (var v in installedList)
                {
                    string displayName = FormatVersionName(v);
                    if (isLowEnd && v.Contains("2021")) displayName += I18n.T("Recommended");
                    displayName += I18n.T("Installed");

                    VersionComboBox.Items.Add(new ComboBoxItem
                    {
                        Content = displayName,
                        Tag = v
                    });
                }
            }

            // If Latest is not installed, keep it available for download
            if (!isLatestInstalled)
            {
                VersionComboBox.Items.Add(new ComboBoxItem
                {
                    Content = I18n.T("LatestVersion"),
                    Tag = "Latest"
                });
            }

            // Online archive versions section
            if (archiveList.Count > 0)
            {
                VersionComboBox.Items.Add(new ComboBoxItem
                {
                    Content = "── Archive ──",
                    Tag = "__HEADER__",
                    IsEnabled = false,
                    Focusable = false,
                    FontSize = 13,
                    FontWeight = FontWeight.Bold,
                    Foreground = new SolidColorBrush(Color.Parse("#A09C9A")),
                    Margin = new Thickness(0, 8, 0, 2)
                });

                foreach (var v in archiveList)
                {
                    string displayName = FormatVersionName(v);
                    if (isLowEnd && v.Contains("2021")) displayName += I18n.T("Recommended");

                    VersionComboBox.Items.Add(new ComboBoxItem
                    {
                        Content = displayName,
                        Tag = v
                    });
                }
            }

            SelectVersionInComboBox(targetSelection);
            _isUpdatingTextProgrammatically = false;

            CheckInstallState();
        }

        private void EnsureValidSelection()
        {
            if (VersionComboBox.SelectedItem == null || (VersionComboBox.SelectedItem is ComboBoxItem cur && (cur.Tag?.ToString() == "__HEADER__" || !cur.IsEnabled)))
            {
                foreach (var item in VersionComboBox.Items)
                {
                    if (item is ComboBoxItem cbi && cbi.Tag?.ToString() != "__HEADER__" && cbi.IsEnabled)
                    {
                        VersionComboBox.SelectedItem = cbi;
                        break;
                    }
                }
            }
        }

        private void VersionComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (!_isInitialized || _isUpdatingTextProgrammatically) return;

            if (VersionComboBox.SelectedItem is ComboBoxItem item)
            {
                if (item.Tag?.ToString() == "__HEADER__" || !item.IsEnabled)
                {
                    SelectVersionInComboBox(_selectedVersion);
                    return;
                }

                string newVersion = item.Tag?.ToString() ?? "Latest";
                AppLogger.Log("UI", $"VersionComboBox_SelectionChanged: '{newVersion}' (Content='{item.Content}')");
                if (_selectedVersion != newVersion)
                {
                    _selectedVersion = newVersion;
                    _operationError = null;
                    _hasUpdateAvailable = false;
                }
                
                CheckInstallState();

                if (_selectedVersion == "Latest")
                {
                    _ = PerformBackgroundUpdateCheckAsync();
                }
            }
        }

        private void DeleteButton_Click(object? sender, RoutedEventArgs e)
        {
            string? gameDir = _updater.GetGameDirectory(_installPath, _selectedVersion);
            bool hasCustom = !string.IsNullOrEmpty(gameDir) &&
                             _updater.VersionSupportsCustomMode(_installPath, _selectedVersion) &&
                             _updater.HasCustomModeFiles(gameDir);

            if (hasCustom && !_updater.IsCustomModeSynchronized(_installPath, _selectedVersion))
            {
                DeleteBackupModal.IsVisible = true;
            }
            else
            {
                DeleteModal.IsVisible = true;
            }
        }

        private void DeleteBackupYesButton_Click(object? sender, RoutedEventArgs e)
        {
            DeleteBackupModal.IsVisible = false;
            string? gameDir = _updater.GetGameDirectory(_installPath, _selectedVersion);
            if (!string.IsNullOrEmpty(gameDir) && Directory.Exists(gameDir))
            {
                AppLogger.Log("Delete", $"Backing up Custom Mode from '{gameDir}' prior to deleting version...");
                _updater.BackupCustomMode(_installPath, gameDir);
                UpdateCustomModeIndicator();
            }
            DeleteModal.IsVisible = true;
        }

        private void DeleteBackupNoButton_Click(object? sender, RoutedEventArgs e)
        {
            DeleteBackupModal.IsVisible = false;
            DeleteModal.IsVisible = true;
        }

        private void DeleteBackupCancelButton_Click(object? sender, RoutedEventArgs e)
        {
            DeleteBackupModal.IsVisible = false;
        }

        private void DeleteModalNoButton_Click(object? sender, RoutedEventArgs e)
        {
            DeleteModal.IsVisible = false;
        }

        private void DeleteModalYesButton_Click(object? sender, RoutedEventArgs e)
        {
            DeleteModal.IsVisible = false;
            if (!_updater.DeleteVersion(_installPath, _selectedVersion, out string error))
            {
                ShowOperationError($"{I18n.T("DeleteError")} {error}");
                return;
            }
            _selectedVersion = "Latest";
            PopulateVersionComboBox("Latest");
            UpdateTexts();
            CheckInstallState();
        }

        private void OpenRunnersFolderButton_Click(object? sender, RoutedEventArgs e)
        {
            string runnersDir = LinuxEngine.GetRunnersDirectory(_installPath);
            if (!System.IO.Directory.Exists(runnersDir)) System.IO.Directory.CreateDirectory(runnersDir);
            
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = "explorer.exe", ArgumentList = { runnersDir }, UseShellExecute = true });
                }
                else if (OperatingSystem.IsMacOS())
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = "open", ArgumentList = { runnersDir }, UseShellExecute = true });
                }
                else if (OperatingSystem.IsLinux())
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = "xdg-open", ArgumentList = { runnersDir }, UseShellExecute = true });
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("Runner", $"Could not open runners folder '{runnersDir}'.", ex);
                ShowOperationError(I18n.T("ErrProton") + ex.Message);
            }
        }

        private bool _isInitialLanguageSetup = false;

        private void ShowLanguageScreen(bool isInitial = false)
        {
            _isInitialLanguageSetup = isInitial;
            MainScreen.IsVisible = false;
            LanguageScreen.IsVisible = true;

            if (isInitial)
            {
                LanguageBackButton.IsVisible = false;
                _detectedLanguageCode = I18n.DetectSystemLanguage();
                SetupLanguagePrompt(_detectedLanguageCode);
                LanguagePromptCard.IsVisible = true;
                LanguageGridPanel.IsVisible = false;
            }
            else
            {
                LanguageBackButton.IsVisible = true;
                LanguagePromptCard.IsVisible = false;
                LanguageGridPanel.IsVisible = true;
            }
        }

        private void SetupLanguagePrompt(string langCode)
        {
            string question = I18n.TForLang(langCode, "LanguagePromptQuestion");
            string yesText = I18n.TForLang(langCode, "LanguagePromptYes");
            string noText = I18n.TForLang(langCode, "LanguagePromptNo");

            LanguagePromptQuestionText.Text = question;
            LanguagePromptYesText.Text = yesText.ToUpperInvariant();
            LanguagePromptNoText.Text = noText.ToUpperInvariant();

            string countryCode = FlagService.GetCountryCodeForLanguage(langCode);
            LanguagePromptFlagFallbackText.Text = FlagService.GetFallbackInitials(langCode);
            LanguagePromptFlagFallback.IsVisible = true;
            LanguagePromptFlagImg.Source = null;

            _ = Task.Run(async () =>
            {
                var bmp = await FlagService.LoadFlagAsync(countryCode);
                if (bmp != null)
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        LanguagePromptFlagImg.Source = bmp;
                        LanguagePromptFlagFallback.IsVisible = false;
                    });
                }
            });
        }

        private void LanguagePromptYesButton_Click(object? sender, RoutedEventArgs e)
        {
            I18n.SetLanguage(_detectedLanguageCode);
            I18n.SaveLanguage(_detectedLanguageCode);
            UpdateTexts();
            CheckInstallState();
            ShowHeartUpdateNotice();
        }

        private void LanguagePromptNoButton_Click(object? sender, RoutedEventArgs e)
        {
            LanguagePromptCard.IsVisible = false;
            LanguageGridPanel.IsVisible = true;
            LanguageBackButton.IsVisible = false;
        }

        private void ShowMainScreen()
        {
            LanguageScreen.IsVisible = false;
            MainScreen.IsVisible = true;
        }

        private void LangButton_Click(object? sender, RoutedEventArgs e)
        {
            ShowLanguageScreen(isInitial: false);
        }

        private void LanguageBackButton_Click(object? sender, RoutedEventArgs e)
        {
            ShowMainScreen();
        }

        private void LangCard_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string langCode)
            {
                I18n.SetLanguage(langCode);
                I18n.SaveLanguage(langCode);
                UpdateTexts();
                CheckInstallState();
                ShowHeartUpdateNotice();
            }
        }

        private DispatcherTimer? _noticePulseTimer;
        private double _noticePulsePhase = 0;
        private ScaleTransform? _noticeHeartScale;

        private void ShowHeartUpdateNotice()
        {
            HeartNoticeText.Text = I18n.T("HeartUpdateNotice");
            HeartNoticeOkButton.Content = I18n.T("HeartUpdateNoticeOk");
            HeartNoticeOverlay.IsVisible = true;
            StartNoticeHeartPulse();
        }

        private void HeartNoticeOkButton_Click(object? sender, RoutedEventArgs e)
        {
            HeartNoticeOverlay.IsVisible = false;
            StopNoticeHeartPulse();
            if (_isInitialLanguageSetup)
            {
                _isInitialLanguageSetup = false;
                ShowMainScreen();
                if (!HasCompletedTutorial)
                {
                    _pendingInitialOrganize = true;
                    StartTutorial();
                }
                else
                {
                    OrganizeOnboardingModal.IsVisible = true;
                }
            }
            else
            {
                ShowMainScreen();
            }
        }

        private void StartNoticeHeartPulse()
        {
            if (HeartNoticeLogo == null) return;
            StopNoticeHeartPulse();
            _noticePulsePhase = 0;
            _noticeHeartScale = new ScaleTransform(1.0, 1.0);
            HeartNoticeLogo.RenderTransform = _noticeHeartScale;
            _noticePulseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
            _noticePulseTimer.Tick += (s, e) =>
            {
                _noticePulsePhase += 1.0;
                int cycle = (int)_noticePulsePhase % 56;
                double sVal = 1.0;
                double opVal = 0.95;

                if (cycle < 10)
                {
                    double t = cycle / 10.0;
                    double beat = Math.Sin(Math.PI * t);
                    sVal = 1.0 + 0.12 * beat;
                    opVal = 0.88 + 0.12 * beat;
                }
                else if (cycle < 15)
                {
                    sVal = 1.0;
                    opVal = 0.95;
                }
                else if (cycle < 24)
                {
                    double t = (cycle - 15) / 9.0;
                    double beat = Math.Sin(Math.PI * t);
                    sVal = 1.0 + 0.06 * beat;
                    opVal = 0.90 + 0.10 * beat;
                }
                else
                {
                    sVal = 1.0;
                    opVal = 0.95;
                }

                if (_noticeHeartScale != null)
                {
                    _noticeHeartScale.ScaleX = sVal;
                    _noticeHeartScale.ScaleY = sVal;
                }
                HeartNoticeLogo.Opacity = opVal;
            };
            _noticePulseTimer.Start();
        }

        private void StopNoticeHeartPulse()
        {
            if (_noticePulseTimer != null)
            {
                _noticePulseTimer.Stop();
                _noticePulseTimer = null;
                _noticePulsePhase = 0;
            }
            if (_noticeHeartScale != null)
            {
                _noticeHeartScale.ScaleX = 1.0;
                _noticeHeartScale.ScaleY = 1.0;
            }
            if (HeartNoticeLogo != null)
            {
                HeartNoticeLogo.Opacity = 1.0;
            }
        }

        public static bool HasCompletedTutorial => LauncherConfig.TutorialCompleted;

        private int _tutorialCurrentStep = 0;
        private const int TotalTutorialSteps = 5;
        private bool _pendingInitialOrganize = false;

        public void StartTutorial()
        {
            ShowMainScreen();
            _tutorialCurrentStep = 0;
            TutorialOverlay.IsVisible = true;
            ShowTutorialStep(0);
        }

        private void TutorialHelpButton_Click(object? sender, RoutedEventArgs e)
        {
            _pendingInitialOrganize = false;
            StartTutorial();
        }

        private void TutorialCloseButton_Click(object? sender, RoutedEventArgs e)
        {
            EndTutorial(markCompleted: true);
        }

        private void TutorialPrevButton_Click(object? sender, RoutedEventArgs e)
        {
            if (_tutorialCurrentStep > 0)
            {
                ShowTutorialStep(_tutorialCurrentStep - 1);
            }
        }

        private void TutorialNextButton_Click(object? sender, RoutedEventArgs e)
        {
            if (_tutorialCurrentStep < TotalTutorialSteps - 1)
            {
                ShowTutorialStep(_tutorialCurrentStep + 1);
            }
            else
            {
                EndTutorial(markCompleted: true);
            }
        }

        private void EndTutorial(bool markCompleted = true)
        {
            TutorialOverlay.IsVisible = false;
            if (markCompleted)
            {
                LauncherConfig.TutorialCompleted = true;
            }

            if (_pendingInitialOrganize)
            {
                _pendingInitialOrganize = false;
                OrganizeOnboardingModal.IsVisible = true;
            }
        }

        private void ShowTutorialStep(int step)
        {
            if (step < 0) step = 0;
            if (step >= TotalTutorialSteps)
            {
                EndTutorial(true);
                return;
            }

            _tutorialCurrentStep = step;
            TutorialOverlay.IsVisible = true;

            TutorialStepBadge.Text = string.Format(I18n.T("TutorialStepBadge"), step + 1, TotalTutorialSteps);
            TutorialSkipButton.Content = I18n.T("TutorialSkip");
            TutorialPrevText.Text = I18n.T("TutorialPrev");
            TutorialPrevButton.IsVisible = step > 0;
            TutorialNextText.Text = step == TotalTutorialSteps - 1
                ? I18n.T("TutorialFinish")
                : I18n.T("TutorialNext");

            string titleKey = $"TutorialStep{step + 1}Title";
            string descKey = $"TutorialStep{step + 1}Desc";
            TutorialTitleText.Text = I18n.T(titleKey);
            TutorialSubtitlePanel.IsVisible = step == 3;
            TutorialDescText.Text = I18n.T(descKey);

            Control? target = step switch
            {
                0 => VersionSelectionContainer,
                1 => CustomModeStatusContainer,
                2 => OrganizeFoldersButton,
                3 => CloseOnLaunchButton,
                4 => AutoUpdateButton,
                _ => null
            };

            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    if (target == null || !target.IsVisible)
                    {
                        TutorialHighlightBorder.IsVisible = false;
                        TutorialMaskPath.Data = null;
                        return;
                    }

                    Point? relativePos = target.TranslatePoint(new Point(0, 0), TutorialOverlay);
                    if (!relativePos.HasValue)
                    {
                        TutorialHighlightBorder.IsVisible = false;
                        TutorialMaskPath.Data = null;
                        return;
                    }

                    double x = relativePos.Value.X;
                    double y = relativePos.Value.Y;
                    double w = target.Bounds.Width;
                    double h = target.Bounds.Height;

                    if (w <= 0 || h <= 0)
                    {
                        w = 200;
                        h = 46;
                    }

                    double pad = 6;
                    x = Math.Max(2, x - pad);
                    y = Math.Max(2, y - pad);
                    w += pad * 2;
                    h += pad * 2;

                    TutorialHighlightBorder.Width = w;
                    TutorialHighlightBorder.Height = h;
                    TutorialHighlightBorder.Margin = new Thickness(x, y, 0, 0);
                    TutorialHighlightBorder.IsVisible = true;

                    double totalW = TutorialOverlay.Bounds.Width > 0 ? TutorialOverlay.Bounds.Width : 391;
                    double totalH = TutorialOverlay.Bounds.Height > 0 ? TutorialOverlay.Bounds.Height : 690;
                    var fullScreen = new RectangleGeometry(new Rect(0, 0, totalW, totalH));
                    var hole = new RectangleGeometry(new Rect(x, y, w, h), 8, 8);
                    TutorialMaskPath.Data = new CombinedGeometry(GeometryCombineMode.Exclude, fullScreen, hole);

                    if (y < 280)
                    {
                        TutorialCard.VerticalAlignment = VerticalAlignment.Bottom;
                        TutorialCard.Margin = new Thickness(0, 0, 0, 80);
                    }
                    else
                    {
                        TutorialCard.VerticalAlignment = VerticalAlignment.Top;
                        TutorialCard.Margin = new Thickness(0, 70, 0, 0);
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Warning("Tutorial", "Error rendering tutorial step.", ex);
                }
            }, DispatcherPriority.Loaded);
        }

        private async Task LoadAllFlagsAsync()
        {
            var items = new (string Code, string CountryCode, Image Img, Border Fallback)[]
            {
                ("en", "us", FlagImg_en, FlagFallback_en),
                ("es", "es", FlagImg_es, FlagFallback_es),
                ("pt", "br", FlagImg_pt, FlagFallback_pt),
                ("tr", "tr", FlagImg_tr, FlagFallback_tr),
                ("ru", "ru", FlagImg_ru, FlagFallback_ru),
                ("fr", "fr", FlagImg_fr, FlagFallback_fr),
                ("th", "th", FlagImg_th, FlagFallback_th),
                ("de", "de", FlagImg_de, FlagFallback_de),
                ("it", "it", FlagImg_it, FlagFallback_it),
                ("id", "id", FlagImg_id, FlagFallback_id),
                ("pl", "pl", FlagImg_pl, FlagFallback_pl),
                ("ja", "jp", FlagImg_ja, FlagFallback_ja),
            };

            await Task.WhenAll(items.Select(async item =>
            {
                var bmp = await FlagService.LoadFlagAsync(item.CountryCode);
                if (bmp != null)
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        item.Img.Source = bmp;
                        item.Fallback.IsVisible = false;
                    });
                }
            }));
        }

        private void UpdateTexts()
        {
            InstallPathLabel.Text = I18n.T("InstallPath");
            
            OldVersionNotice.Text = OperatingSystem.IsWindows() ? I18n.T("OldVerNoticeWindows") : I18n.T("OldVerNotice");
            Avalonia.Controls.ToolTip.SetTip(OpenRunnersFolderButton, I18n.T("OpenRunners"));
            UpdateCloseOnLaunchUI();
            Avalonia.Controls.ToolTip.SetTip(TutorialHelpButton, I18n.T("TutorialHelpTooltip"));
            if (AutoUpdateButton != null)
            {
                Avalonia.Controls.ToolTip.SetTip(AutoUpdateButton, I18n.T("AutoUpdateOption"));
                Avalonia.Controls.ToolTip.SetPlacement(AutoUpdateButton, Avalonia.Controls.PlacementMode.Top);
                Avalonia.Controls.ToolTip.SetVerticalOffset(AutoUpdateButton, -8);
                Avalonia.Controls.ToolTip.SetShowDelay(AutoUpdateButton, 200);
            }
            if (LatestPostButton != null)
            {
                Avalonia.Controls.ToolTip.SetTip(LatestPostButton, I18n.T("LatestPostTooltip"));
                Avalonia.Controls.ToolTip.SetPlacement(LatestPostButton, Avalonia.Controls.PlacementMode.Top);
                Avalonia.Controls.ToolTip.SetVerticalOffset(LatestPostButton, -8);
                Avalonia.Controls.ToolTip.SetShowDelay(LatestPostButton, 200);
            }
            if (YouTubeButton != null)
            {
                Avalonia.Controls.ToolTip.SetTip(YouTubeButton, I18n.T("YouTubeTooltip"));
            }
            if (YouTubePrimaryItem != null)
            {
                YouTubePrimaryItem.Header = I18n.T("YouTubePrimary");
            }
            if (YouTubeSecondaryItem != null)
            {
                YouTubeSecondaryItem.Header = I18n.T("YouTubeSecondary");
            }
            if (DiscordButton != null)
            {
                Avalonia.Controls.ToolTip.SetTip(DiscordButton, I18n.T("DiscordTooltip"));
            }
            if (KickButton != null)
            {
                Avalonia.Controls.ToolTip.SetTip(KickButton, I18n.T("KickTooltip"));
            }
            if (_isPoseModShiftHeld && CanLaunchPoseMod())
            {
                Avalonia.Controls.ToolTip.SetTip(PlayButton, I18n.T("PoseModSavedTooltip"));
            }
            NotificationModalTitle.Text = I18n.T("NotificationModalTitle");
            NotificationModalDesc.Text = I18n.T("NotificationModalDesc");
            NotificationModalYesButton.Content = I18n.T("NotificationModalYes");
            NotificationModalNoButton.Content = I18n.T("NotificationModalNo");
            ShortcutModalTitle.Text = I18n.T("ShortcutModalTitle");
            ShortcutModalDesc.Text = I18n.T("ShortcutModalDesc");
            ShortcutModalYesButton.Content = I18n.T("ShortcutModalYes");
            ShortcutModalNoButton.Content = I18n.T("ShortcutModalNo");

            PermissionDeniedModalTitle.Text = I18n.T("PermissionDeniedModalTitle");
            PermissionDeniedModalDesc.Text = I18n.T("PermissionDeniedModalDesc");
            PermissionDeniedModalSettingsButton.Content = I18n.T("PermissionDeniedModalSettings");
            PermissionDeniedModalCloseButton.Content = I18n.T("PermissionDeniedModalClose");

            DeleteBackupModalTitle.Text = I18n.T("DeleteBackupModalTitle");
            DeleteBackupModalDesc.Text = I18n.T("DeleteBackupModalDesc");
            DeleteBackupYesButton.Content = I18n.T("DeleteBackupModalYes");
            DeleteBackupNoButton.Content = I18n.T("DeleteBackupModalNo");
            DeleteBackupCancelButton.Content = I18n.T("Cancel");

            DeleteModalTitle.Text = I18n.T("DeleteModalTitle");
            DeleteModalDesc.Text = I18n.T("DeleteModalDesc");
            DeleteModalSafeNotice.Text = I18n.T("DeleteModalSafeNotice");
            DeleteModalYesButton.Content = I18n.T("DeleteModalYes");
            DeleteModalNoButton.Content = I18n.T("DeleteModalNo");

            UpdateConfirmTitle.Text = I18n.T("UpdateConfirmTitle");
            UpdateConfirmDesc.Text = I18n.T("UpdateConfirmDesc");
            UpdateConfirmCustomModeSafe.Text = I18n.T("UpdateConfirmCustomModeSafe");
            UpdateConfirmYesButton.Content = I18n.T("UpdateConfirmYes");
            UpdateConfirmNoButton.Content = I18n.T("Cancel");

            ImportCustomModeTitle.Text = I18n.T("ImportCustomModeTitle");
            ImportCustomModeDesc.Text = I18n.T("ImportCustomModeDesc");
            ImportCustomModeYesButton.Content = I18n.T("ImportCustomModeYes");
            ImportCustomModeNoButton.Content = I18n.T("ImportCustomModeNo");
            ImportCustomModeCancelButton.Content = I18n.T("Cancel");
            UpdateCustomModeIndicator();

            ProtonModalTitle.Text = I18n.T("ProtonModalTitle");
            ProtonModalDesc1.Text = I18n.T("ProtonModalDesc1");
            ProtonModalDesc2.Text = I18n.T("ProtonModalDesc2");
            ProtonIsolatedButton.Content = I18n.T("ProtonIsolated");
            ProtonSteamButton.Content = I18n.T("ProtonSteam");
            ProtonCancelButton.Content = I18n.T("Cancel");

            HeartNoticeText.Text = I18n.T("HeartUpdateNotice");
            HeartNoticeOkButton.Content = I18n.T("HeartUpdateNoticeOk");

            OrganizeFoldersButtonText.Text = I18n.T("OrganizeBtn");
            OrganizeOnboardingTitle.Text = I18n.T("OrganizeOnboardingTitle");
            OrganizeOnboardingDesc.Text = I18n.T("OrganizeOnboardingDesc");
            OrganizeOnboardingYesButton.Content = I18n.T("OrganizeOnboardingYes");
            OrganizeOnboardingNoButton.Content = I18n.T("OrganizeOnboardingNo");

            OrganizePromptTitle.Text = I18n.T("OrganizePromptTitle");
            OrganizePromptDesc.Text = I18n.T("OrganizePromptDesc");
            OrganizePromptSelectButton.Content = I18n.T("OrganizePromptSelect");
            OrganizePromptCancelButton.Content = I18n.T("Cancel");

            OrganizePreviewTitle.Text = I18n.T("OrganizePreviewTitle");
            OrganizePreviewDesc.Text = I18n.T("OrganizePreviewDesc");
            OrganizeConfirmButton.Content = I18n.T("OrganizePreviewConfirm");
            OrganizeCancelButton.Content = I18n.T("Cancel");

            if (TutorialOverlay != null && TutorialOverlay.IsVisible)
            {
                ShowTutorialStep(_tutorialCurrentStep);
            }

            PopulateVersionComboBox(_selectedVersion);

            if ((OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) && LinuxRunnerComboBox.ItemsSource != null)
            {
                var selectedRunnerPath = (LinuxRunnerComboBox.SelectedItem as LinuxRunner)?.Path;
                InitializeLinuxRunners();
                if (selectedRunnerPath != null)
                {
                    var newSelection = System.Linq.Enumerable.FirstOrDefault(
                        System.Linq.Enumerable.OfType<LinuxRunner>(LinuxRunnerComboBox.ItemsSource), 
                        r => r.Path == selectedRunnerPath);
                    if (newSelection != null) LinuxRunnerComboBox.SelectedItem = newSelection;
                }
            }

            CheckInstallState();
        }

        private Task PerformBackgroundUpdateCheckAsync()
        {
            if (_selectedVersion != "Latest" || _operationDepth > 0 || _isPlaying)
                return Task.CompletedTask;
            if (!_backgroundUpdateTask.IsCompleted) return _backgroundUpdateTask;
            var cancellation = new CancellationTokenSource();
            _backgroundUpdateCancellation = cancellation;
            return _backgroundUpdateTask = CheckForUpdatesInBackgroundAsync(_installPath, cancellation);
        }

        private async Task CheckForUpdatesInBackgroundAsync(string installPath, CancellationTokenSource cancellation)
        {
            _isCheckingUpdates = true;
            CheckInstallState();
            try
            {
                UpdateCheckResult result = await _updater.CheckForUpdatesAsync(installPath, cancellation.Token);
                if (_installPath == installPath && _selectedVersion == "Latest")
                {
                    _hasUpdateAvailable = result == UpdateCheckResult.UpdateAvailable;
                    if (result == UpdateCheckResult.Failed)
                    {
                        AppLogger.Warning("UpdateCheck", $"Background update check did not succeed: {_updater.LastError}");
                    }
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                AppLogger.Log("UpdateCheck", "Background update check cancelled.");
            }
            catch (Exception ex)
            {
                AppLogger.Warning("UpdateCheck", "Background update check encountered an error.", ex);
            }
            finally
            {
                if (ReferenceEquals(_backgroundUpdateCancellation, cancellation))
                    _backgroundUpdateCancellation = null;
                cancellation.Dispose();
                _isCheckingUpdates = false;
                CheckInstallState();
                UpdateLatestVersionComboBoxItem();
            }
        }

        private void UpdateLatestVersionComboBoxItem()
        {
            if (!Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.Post(UpdateLatestVersionComboBoxItem);
                return;
            }

            foreach (var item in VersionComboBox.Items)
            {
                if (item is ComboBoxItem cbi && (cbi.Tag == null || cbi.Tag.ToString() == "Latest"))
                {
                    cbi.Tag = "Latest";
                    string text = I18n.T("LatestVersion");
                    if (_updater.IsGameInstalled(_installPath, "Latest") && !_hasUpdateAvailable)
                    {
                        text += I18n.T("Installed");
                    }
                    cbi.Content = text;
                    break;
                }
            }
        }

        private void BeginOperation()
        {
            if (_operationDepth++ == 0) _operationError = null;
            CheckInstallState();
        }

        private void EndOperation()
        {
            _operationDepth--;
            if (_operationDepth == 0) DownloadProgressBar.IsVisible = false;
            CheckInstallState();
        }

        private void ShowOperationError(string message)
        {
            _operationError = message;
            StatusText.Text = message;
            SetStatusStyle("Error");
        }

        private void CloseButton_Click(object? sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private void Header_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            var props = e.GetCurrentPoint(this).Properties;
            if (props.IsLeftButtonPressed)
            {
                this.BeginMoveDrag(e);
            }
            else if (props.IsRightButtonPressed)
            {
                ShowLanguageScreen(isInitial: false);
            }
        }

        
        private void SetStatusStyle(string state)
        {
            if (StatusBorder == null || StatusText == null) return;
            switch (state)
            {
                case "Updated":
                    StatusBorder.Background = new SolidColorBrush(Color.Parse("#A5D6A7"));
                    StatusText.Foreground = new SolidColorBrush(Color.Parse("#3D3330"));
                    break;
                case "Outdated":
                    StatusBorder.Background = new SolidColorBrush(Color.Parse("#FCE68A"));
                    StatusText.Foreground = new SolidColorBrush(Color.Parse("#3D3330"));
                    break;
                case "Error":
                    StatusBorder.Background = new SolidColorBrush(Color.Parse("#EF9A9A"));
                    StatusText.Foreground = new SolidColorBrush(Color.Parse("#3D3330"));
                    break;
                case "Default":
                default:
                    StatusBorder.Background = new SolidColorBrush(Color.Parse("#A3857B"));
                    StatusText.Foreground = Brushes.White;
                    break;
            }
        }

        private void SetLogoPulsing(bool pulsing)
        {
            if (LauncherLogo == null) return;
            if (pulsing)
            {
                if (_pulseTimer == null)
                {
                    _pulsePhase = 0;
                    _logoScale = new ScaleTransform(1.0, 1.0);
                    LauncherLogo.RenderTransform = _logoScale;
                    _pulseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(45) };
                    _pulseTimer.Tick += (s, e) =>
                    {
                        _pulsePhase += 1.0;
                        int cycle = (int)_pulsePhase % 32;
                        double sVal = 1.0;
                        double opVal = 0.95;

                        if (cycle < 6)
                        {
                            double t = cycle / 6.0;
                            double beat = Math.Sin(Math.PI * t);
                            sVal = 1.0 + 0.08 * beat;
                            opVal = 0.88 + 0.12 * beat;
                        }
                        else if (cycle < 9)
                        {
                            sVal = 1.0;
                            opVal = 0.88;
                        }
                        else if (cycle < 14)
                        {
                            double t = (cycle - 9) / 5.0;
                            double beat = Math.Sin(Math.PI * t);
                            sVal = 1.0 + 0.045 * beat;
                            opVal = 0.90 + 0.10 * beat;
                        }
                        else
                        {
                            sVal = 1.0;
                            opVal = 0.95;
                        }

                        if (_logoScale != null)
                        {
                            _logoScale.ScaleX = sVal;
                            _logoScale.ScaleY = sVal;
                        }
                        LauncherLogo.Opacity = opVal;
                    };
                    _pulseTimer.Start();
                }
            }
            else
            {
                if (_pulseTimer != null)
                {
                    _pulseTimer.Stop();
                    _pulseTimer = null;
                    _pulsePhase = 0;
                    if (_logoScale != null)
                    {
                        _logoScale.ScaleX = 1.0;
                        _logoScale.ScaleY = 1.0;
                    }
                    LauncherLogo.Opacity = 1.0;
                }
            }
        }

        private void SetLoadingHeartPulsing(bool pulsing)
        {
            if (LoadingHeartIcon == null) return;

            if (pulsing)
            {
                if (_loadingHeartTimer == null)
                {
                    _loadingHeartFrame = 0;
                    _loadingHeartScale = new ScaleTransform(1.0, 1.0);
                    _loadingHeartRotate = new RotateTransform(0.0);
                    _loadingTransformGroup = new TransformGroup();
                    _loadingTransformGroup.Children.Add(_loadingHeartScale);
                    _loadingTransformGroup.Children.Add(_loadingHeartRotate);
                    LoadingHeartIcon.RenderTransform = _loadingTransformGroup;

                    _loadingHeartTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
                    _loadingHeartTimer.Tick += (s, e) =>
                    {
                        _loadingHeartFrame++;
                        int cycleFrame = _loadingHeartFrame % 76;

                        if (cycleFrame < 40)
                        {
                            double t = cycleFrame / 40.0;
                            double eased = 0.5 - 0.5 * Math.Cos(Math.PI * t);
                            double angle = 360.0 * eased;

                            if (_loadingHeartRotate != null)
                                _loadingHeartRotate.Angle = angle;

                            if (_loadingHeartScale != null)
                            {
                                _loadingHeartScale.ScaleX = 1.0;
                                _loadingHeartScale.ScaleY = 1.0;
                            }

                            if (LoadingHeartIcon != null)
                                LoadingHeartIcon.Opacity = 0.95;
                        }
                        else
                        {
                            if (_loadingHeartRotate != null)
                                _loadingHeartRotate.Angle = 0.0;

                            double p = (cycleFrame - 40) / 36.0;
                            double sVal = 1.0;
                            double opVal = 0.95;

                            if (p < 0.45)
                            {
                                double t1 = p / 0.45;
                                double beat = Math.Sin(Math.PI * t1);
                                sVal = 1.0 + 0.28 * beat;
                                opVal = 0.90 + 0.10 * beat;
                            }
                            else if (p < 0.55)
                            {
                                sVal = 1.0;
                                opVal = 0.90;
                            }
                            else if (p < 0.90)
                            {
                                double t2 = (p - 0.55) / 0.35;
                                double beat = Math.Sin(Math.PI * t2);
                                sVal = 1.0 + 0.15 * beat;
                                opVal = 0.90 + 0.10 * beat;
                            }
                            else
                            {
                                sVal = 1.0;
                                opVal = 0.95;
                            }

                            if (_loadingHeartScale != null)
                            {
                                _loadingHeartScale.ScaleX = sVal;
                                _loadingHeartScale.ScaleY = sVal;
                            }

                            if (LoadingHeartIcon != null)
                                LoadingHeartIcon.Opacity = opVal;
                        }
                    };
                    _loadingHeartTimer.Start();
                }
            }
            else
            {
                if (_loadingHeartTimer != null)
                {
                    _loadingHeartTimer.Stop();
                    _loadingHeartTimer = null;
                    _loadingHeartFrame = 0;

                    if (_loadingHeartScale != null)
                    {
                        _loadingHeartScale.ScaleX = 1.0;
                        _loadingHeartScale.ScaleY = 1.0;
                    }
                    if (_loadingHeartRotate != null)
                    {
                        _loadingHeartRotate.Angle = 0.0;
                    }
                    if (LoadingHeartIcon != null)
                    {
                        LoadingHeartIcon.Opacity = 1.0;
                    }
                }
            }
        }

        private void ShowLoadingOverlay(bool show)
        {
            if (LoadingOverlay != null)
            {
                LoadingOverlay.IsVisible = show;
            }
            SetLoadingHeartPulsing(show);
        }

        private void CheckInstallState()
        {
            UpdateCustomModeIndicator();
            if (_operationDepth > 0)
            {
                bool canCancelDownload = _downloadCancellation != null && !_downloadCancellation.IsCancellationRequested;
                PlayButton.Classes.Set("posemod", false);
                PlayButton.IsEnabled = DeleteButton.IsEnabled = false;
                UpdateButton.IsEnabled = canCancelDownload;
                UpdateButton.Background = new SolidColorBrush(Color.Parse(canCancelDownload ? "#E53935" : "#D3CBC8"));
                if (_downloadCancellation != null)
                    UpdateText.Text = I18n.T(canCancelDownload ? "CancelDownload" : "CancellingDownload");
                VersionComboBox.IsEnabled = LinuxRunnerComboBox.IsEnabled = false;
                BrowseButton.IsEnabled = PathTextBox.IsEnabled = false;
                return;
            }
            UpdateButton.IsEnabled = !_isPlaying && !_isCheckingUpdates;
            var installedMap = _updater.GetInstalledVersionsMap(_installPath);
            var info = _updater.GetInstallationInfo(_installPath, _selectedVersion, installedMap);
            bool isInstalled = info.IsInstalled;
            string? gameDir = info.GameDirectory;
            string? gameExe = info.ExecutablePath;
            DateTime? buildDate = info.BuildDate;
            string currentState = $"ver='{_selectedVersion}', installed={isInstalled}, exe='{gameExe}', dir='{gameDir}', playing={_isPlaying}";
            if (_lastLoggedState != currentState)
            {
                _lastLoggedState = currentState;
                AppLogger.Log("State", $"CheckInstallState: {currentState}");
            }

            if (OldVersionNotice != null)
            {
                OldVersionNotice.IsVisible = (_selectedVersion != "Latest" && !isInstalled);
            }

            if (ExecutablePathText != null)
            {
                if (isInstalled)
                {
                    ExecutablePathText.Text = $"EXE: {gameExe}";
                    ExecutablePathText.Foreground = new SolidColorBrush(Color.Parse("#4A88C9"));
                }
                else
                {
                    ExecutablePathText.Text = I18n.T("ExeNotFound");
                    ExecutablePathText.Foreground = new SolidColorBrush(Color.Parse("#8C8886"));
                }
            }

            if (isInstalled)
            {
                if (_isPlaying)
                {
                    PlayButton.Classes.Set("posemod", false);
                    PlayButton.Classes.Set("playing", !_isStopping);
                    PlayButton.Classes.Set("stopping", _isStopping);
                    PlayButton.IsEnabled = !_isStopping;
                    PlayButton.Cursor = new Avalonia.Input.Cursor(_isStopping ? Avalonia.Input.StandardCursorType.Wait : Avalonia.Input.StandardCursorType.Hand);
                    PlayText.Text = _isStopping ? I18n.T("Stopping") : I18n.T("Playing");
                    PlayText.Foreground = Brushes.White;
                    PlayIcon.Data = Avalonia.Media.Geometry.Parse("M6 6h12v12H6z");
                    PlayIcon.Foreground = Brushes.White;
                    PlayButton.Background = new SolidColorBrush(Color.Parse(_isStopping ? "#B71C1C" : "#E53935"));
                    PlayButton.Foreground = Brushes.White;

                    UpdateButton.IsEnabled = false;
                    DeleteButton.IsEnabled = false;
                    DeleteButton.Background = new SolidColorBrush(Color.Parse("#D3CBC8"));
                    VersionComboBox.IsEnabled = false;
                    LinuxRunnerComboBox.IsEnabled = false;
                    BrowseButton.IsEnabled = false;
                    PathTextBox.IsEnabled = false;
                }
                else
                {
                    PlayButton.Classes.Set("playing", false);
                    PlayButton.Classes.Set("stopping", false);
                    PlayButton.IsEnabled = true;

                    bool poseModActive = _isPoseModShiftHeld && CanLaunchPoseMod();
                    PlayButton.Classes.Set("posemod", poseModActive);
                    PlayText.Text = poseModActive ? "POSEMOD" : I18n.T("Play");
                    PlayText.Foreground = Brushes.White;
                    PlayIcon.Data = Avalonia.Media.Geometry.Parse("M8 5v14l11-7z");
                    PlayIcon.Foreground = Brushes.White;
                    PlayButton.Background = new SolidColorBrush(Color.Parse(poseModActive ? "#E53935" : "#E88CA6"));
                    PlayButton.Foreground = Brushes.White;
                    if (!poseModActive)
                    {
                        Avalonia.Controls.ToolTip.SetIsOpen(PlayButton, false);
                        Avalonia.Controls.ToolTip.SetTip(PlayButton, null);
                    }

                    VersionComboBox.IsEnabled = true;
                    LinuxRunnerComboBox.IsEnabled = true;
                    BrowseButton.IsEnabled = true;
                    PathTextBox.IsEnabled = true;
                    
                    bool canDelete = _selectedVersion == "Latest"
                        ? _updater.IsGameInstalled(_installPath, "Latest")
                        : true;

                    DeleteButton.IsEnabled = canDelete;
                    DeleteButton.Background = canDelete
                        ? new SolidColorBrush(Color.Parse("#E88CA6"))
                        : new SolidColorBrush(Color.Parse("#D3CBC8"));
                }    
                if (_selectedVersion == "Latest")
                {
                    if (_isCheckingUpdates)
                    {
                        StatusText.Text = I18n.T("Checking");
                        SetStatusStyle("Default");
                        UpdateText.Text = I18n.T("Update");
                    }
                    else if (_hasUpdateAvailable)
                    {
                        StatusText.Text = I18n.T("NewVersion");
                        SetStatusStyle("Outdated");
                        UpdateText.Text = I18n.T("Update");
                    }
                    else if (buildDate == null)
                    {
                        StatusText.Text = I18n.T("NoSupportUpdate");
                        SetStatusStyle("Updated");
                        UpdateText.Text = I18n.T("Reinstall");
                    }
                    else
                    {
                        StatusText.Text = I18n.T("Updated");
                        SetStatusStyle("Updated");
                        UpdateText.Text = I18n.T("Reinstall");
                    }
                }
                else
                {
                    StatusText.Text = (buildDate == null) ? I18n.T("NoSupportUpdate") : I18n.T("ArchiveInstalled");
                    SetStatusStyle("Updated");
                    UpdateText.Text = I18n.T("Reinstall");
                }
            }
            else
            {
                PlayButton.Classes.Set("playing", false);
                PlayButton.Classes.Set("stopping", false);
                PlayButton.Classes.Set("posemod", false);
                PlayButton.IsEnabled = false;
                PlayText.Text = I18n.T("Play");
                PlayText.Foreground = new SolidColorBrush(Color.Parse("#8C8886"));
                PlayIcon.Data = Avalonia.Media.Geometry.Parse("M8 5v14l11-7z");
                PlayIcon.Foreground = new SolidColorBrush(Color.Parse("#8C8886"));
                VersionComboBox.IsEnabled = true;
                LinuxRunnerComboBox.IsEnabled = true;
                BrowseButton.IsEnabled = true;
                PathTextBox.IsEnabled = true;
                
                DeleteButton.IsEnabled = false;
                DeleteButton.Background = new SolidColorBrush(Color.Parse("#D3CBC8"));

                if (_selectedVersion == "Latest")
                {
                    StatusText.Text = I18n.T("NoLatestInDir");
                    SetStatusStyle("Outdated");
                }
                else
                {
                    StatusText.Text = I18n.T("NotFound");
                    SetStatusStyle("Default");
                }

                PlayButton.Background = new SolidColorBrush(Color.Parse("#D3CBC8"));
                PlayButton.Foreground = new SolidColorBrush(Color.Parse("#8C8886"));
                UpdateText.Text = I18n.T("Download");
            }

            bool hasLatestOnDisk = installedMap.ContainsKey("Latest") && !_hasUpdateAvailable;
            SetLogoPulsing(!hasLatestOnDisk);
            PlayButton.IsEnabled &= !_isStopping;
            if (_operationError != null) ShowOperationError(_operationError);
        }

        private void PathTextBox_TextChanged(object? sender, TextChangedEventArgs e)
        {
            if (!_isInitialized || _isUpdatingTextProgrammatically) return;
            _operationError = null;

            if (PathTextBox.Text != null)
            {
                _installPath = PathTextBox.Text.Trim();
                try 
                { 
                    CheckInstallState(); 
                } 
                catch (Exception ex)
                {
                    AppLogger.Warning("Path", $"Could not refresh installation state for '{_installPath}'.", ex);
                }
            }
        }

        private void PathTextBox_LostFocus(object? sender, RoutedEventArgs e)
        {
            CommitPathTextBox();
        }

        private void PathTextBox_KeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                CommitPathTextBox();
                FocusManager?.Focus(null);
            }
        }

        private async void CommitPathTextBox() => await CommitPathTextBoxAsync();

        private async Task CommitPathTextBoxAsync()
        {
            if (string.IsNullOrWhiteSpace(PathTextBox.Text)) return;
            string previousPath = _installPath;
            string raw = PathTextBox.Text.Trim();
            string? detectedVersion = null;
            if (Directory.Exists(raw))
            {
                _installPath = _updater.NormalizeInstallPath(raw, out detectedVersion);
            }
            else
            {
                _installPath = raw.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }

            bool pathChanged = !string.Equals(previousPath, _installPath, StringComparison.OrdinalIgnoreCase);

            LauncherConfig.InstallPath = _installPath;
            _isUpdatingTextProgrammatically = true;
            PathTextBox.Text = _installPath;
            _isUpdatingTextProgrammatically = false;

            AppLogger.Log("UI", $"CommitPathTextBox: set to '{_installPath}' (detected='{detectedVersion}', pathChanged={pathChanged})");

            await RefreshVersionsForCurrentPathAsync(detectedVersion);

            CheckInstallState();
            if (_selectedVersion == "Latest") _ = PerformBackgroundUpdateCheckAsync();

            if ((OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) && pathChanged)
            {
                InitializeLinuxRunners();
            }
        }

        private async Task RefreshVersionsForCurrentPathAsync(string? targetVersionToSelect = null)
        {
            AppLogger.Log("Refresh", $"RefreshVersionsForCurrentPath for '{_installPath}'");
            if (string.IsNullOrWhiteSpace(_installPath) || !Directory.Exists(_installPath)) return;

            ShowLoadingOverlay(true);
            try
            {
                string path = _installPath;
                await Task.Run(() =>
                {
                    _updater.MigrateOldInstall(path);
                    _updater.GetInstalledVersionsMap(path, forceRefresh: true);
                });

                PopulateVersionComboBox(targetVersionToSelect ?? _selectedVersion);
                if (!string.IsNullOrEmpty(targetVersionToSelect))
                {
                    SelectVersionInComboBox(targetVersionToSelect);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("Refresh", "Error refreshing versions for path", ex);
            }
            finally
            {
                ShowLoadingOverlay(false);
            }
        }

        private async void BrowseButton_Click(object? sender, RoutedEventArgs e)
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = I18n.T("SelectFolder"),
                AllowMultiple = false
            });

            if (folders == null || folders.Count == 0) return;

            string? selectedPath = folders[0].TryGetLocalPath();
            if (!string.IsNullOrEmpty(selectedPath))
            {
                if (OperatingSystem.IsMacOS())
                {
                    try
                    {
                        // Test read/write access for macOS permissions
                        string testFile = Path.Combine(selectedPath, ".test_perm_" + Environment.TickCount64);
                        File.WriteAllText(testFile, "ok");
                        File.Delete(testFile);
                    }
                    catch (UnauthorizedAccessException)
                    {
                        AppLogger.Error("Browse", $"Access denied by macOS TCC for path: '{selectedPath}'");
                        try
                        {
                            // Reset TCC permissions so macOS can re-prompt on subsequent browse attempts
                            Process.Start(new ProcessStartInfo
                            {
                                FileName = "tccutil",
                                Arguments = "reset SystemPolicyDocumentsFolder com.yanderesimulator.launcher",
                                UseShellExecute = false,
                                CreateNoWindow = true
                            })?.WaitForExit(1000);
                        }
                        catch { }

                        PermissionDeniedModal.IsVisible = true;
                        return;
                    }
                    catch
                    {
                        // Ignore transient I/O inspection errors
                    }
                }

                string normalizedPath = _updater.NormalizeInstallPath(selectedPath, out string? detectedVersion);
                _installPath = normalizedPath;
                
                _isUpdatingTextProgrammatically = true;
                PathTextBox.Text = _installPath;
                _isUpdatingTextProgrammatically = false;
                
                LauncherConfig.InstallPath = _installPath;
                
                await RefreshVersionsForCurrentPathAsync(detectedVersion);

                CheckInstallState();
                if (_selectedVersion == "Latest") _ = PerformBackgroundUpdateCheckAsync();

                if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                {
                    InitializeLinuxRunners();
                }
            }
        }

        private void SelectVersionInComboBox(string? detectedVersion)
        {
            if (string.IsNullOrEmpty(detectedVersion))
            {
                EnsureValidSelection();
                return;
            }

            if (!detectedVersion.Equals("Latest", StringComparison.OrdinalIgnoreCase))
            {
                var installedMap = _updater.GetInstalledVersionsMap(_installPath);
                if (installedMap.TryGetValue("Latest", out var latestDir))
                {
                    string? detectedDir = _updater.GetGameDirectory(_installPath, detectedVersion, installedMap);
                    if (detectedDir != null && string.Equals(detectedDir, latestDir, StringComparison.OrdinalIgnoreCase))
                    {
                        detectedVersion = "Latest";
                    }
                }
            }

            if (detectedVersion.Equals("Latest", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var item in VersionComboBox.Items)
                {
                    if (item is ComboBoxItem cbi && cbi.Tag?.ToString() == "Latest")
                    {
                        VersionComboBox.SelectedItem = cbi;
                        _pendingInitialVersion = null;
                        return;
                    }
                }
                EnsureValidSelection();
                return;
            }

            foreach (var item in VersionComboBox.Items)
            {
                if (item is ComboBoxItem cbi && cbi.Tag?.ToString() != "__HEADER__" && string.Equals(cbi.Tag?.ToString(), detectedVersion, StringComparison.OrdinalIgnoreCase))
                {
                    VersionComboBox.SelectedItem = cbi;
                    _pendingInitialVersion = null;
                    return;
                }
            }

            var catalogVersions = new System.Collections.Generic.List<string>();
            foreach (var item in VersionComboBox.Items)
            {
                if (item is ComboBoxItem cbi && cbi.Tag is string tag && tag != "Latest" && tag != "__HEADER__")
                {
                    catalogVersions.Add(tag);
                }
            }

            if (catalogVersions.Count == 0)
            {
                _pendingInitialVersion = detectedVersion;
                EnsureValidSelection();
                return;
            }

            string? matchedTag = _updater.FindVersionInCatalog(catalogVersions, detectedVersion, _installPath);
            if (matchedTag != null)
            {
                foreach (var item in VersionComboBox.Items)
                {
                    if (item is ComboBoxItem cbi && cbi.Tag?.ToString() == matchedTag)
                    {
                        VersionComboBox.SelectedItem = cbi;
                        _pendingInitialVersion = null;
                        return;
                    }
                }
            }
            else
            {
                _pendingInitialVersion = detectedVersion;
            }

            EnsureValidSelection();
        }

        private async void UpdateButton_Click(object? sender, RoutedEventArgs e)
        {
            if (_downloadCancellation != null)
            {
                if (!_downloadCancellation.IsCancellationRequested)
                {
                    AppLogger.Log("Download", "Download cancellation requested by the user.");
                    _downloadCancellation.Cancel();
                    _backgroundUpdateCancellation?.Cancel();
                    CheckInstallState();
                }
                return;
            }

            if (_operationDepth > 0 || _isPlaying) return;
            await CommitPathTextBoxAsync();

            bool isInstalled = _updater.IsGameInstalled(_installPath, _selectedVersion);
            if (isInstalled)
            {
                bool supportsCustomMode = _updater.VersionSupportsCustomMode(_installPath, _selectedVersion);
                UpdateConfirmCustomModeSafe.IsVisible = supportsCustomMode;
                UpdateConfirmModal.IsVisible = true;
                return;
            }

            string backupPath = GameUpdater.GetCustomModeBackupPath(_installPath);
            bool hasBackup = File.Exists(backupPath);
            bool versionSupportsCustomMode = _updater.VersionSupportsCustomMode(_installPath, _selectedVersion);

            if (hasBackup && versionSupportsCustomMode)
            {
                ImportCustomModeModal.IsVisible = true;
                return;
            }

            await StartDownloadOrUpdateAsync(isUpdate: false, restoreBackup: false);
        }

        private async void ImportCustomModeYesButton_Click(object? sender, RoutedEventArgs e)
        {
            ImportCustomModeModal.IsVisible = false;
            await StartDownloadOrUpdateAsync(isUpdate: false, restoreBackup: true);
        }

        private async void ImportCustomModeNoButton_Click(object? sender, RoutedEventArgs e)
        {
            ImportCustomModeModal.IsVisible = false;
            await StartDownloadOrUpdateAsync(isUpdate: false, restoreBackup: false);
        }

        private void ImportCustomModeCancelButton_Click(object? sender, RoutedEventArgs e)
        {
            ImportCustomModeModal.IsVisible = false;
        }

        private async void UpdateConfirmYesButton_Click(object? sender, RoutedEventArgs e)
        {
            UpdateConfirmModal.IsVisible = false;
            await StartDownloadOrUpdateAsync(isUpdate: true);
        }

        private void UpdateConfirmNoButton_Click(object? sender, RoutedEventArgs e)
        {
            UpdateConfirmModal.IsVisible = false;
        }

        private async void CustomModeStatusContainer_PointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
        {
            if (_isSyncingCustomMode) return;

            try
            {
                _isSyncingCustomMode = true;

                if (CustomModeStatusContainer != null)
                {
                    CustomModeStatusContainer.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Wait);
                    string syncingText = $"> {I18n.T("SyncingCustomMode")}";
                    Avalonia.Controls.ToolTip.SetTip(CustomModeStatusContainer, syncingText);
                    if (CustomModeStatusIcon != null)
                    {
                        CustomModeStatusIcon.Data = Avalonia.Media.Geometry.Parse("M12 4V1L8 5l4 4V6c3.31 0 6 2.69 6 6 0 1.01-.25 1.97-.7 2.8l1.46 1.46C19.54 15.03 20 13.57 20 12c0-4.42-3.58-8-8-8zm0 14c-3.31 0-6-2.69-6-6 0-1.01.25-1.97.7-2.8L5.24 7.74C4.46 8.97 4 10.43 4 12c0 4.42 3.58 8 8 8v3l4-4-4-4v3z");
                        CustomModeStatusIcon.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#4A88C9"));
                        Avalonia.Controls.ToolTip.SetTip(CustomModeStatusIcon, syncingText);
                    }
                }

                await Task.Run(async () =>
                {
                    var sw = Stopwatch.StartNew();
                    _updater.VerifyAndSyncCustomModeWithHash(_installPath, _selectedVersion);
                    if (sw.ElapsedMilliseconds < 500)
                    {
                        await Task.Delay(500 - (int)sw.ElapsedMilliseconds);
                    }
                });
            }
            catch (Exception ex)
            {
                AppLogger.Error("MainWindow", "Error during manual Custom Mode sync.", ex);
            }
            finally
            {
                _isSyncingCustomMode = false;
                if (CustomModeStatusContainer != null)
                {
                    CustomModeStatusContainer.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);
                }
                UpdateCustomModeIndicator();
            }
        }

        private void UpdateCustomModeIndicator()
        {
            if (_isSyncingCustomMode) return;
            try
            {
                string? backupPath = !string.IsNullOrWhiteSpace(_installPath)
                    ? GameUpdater.GetCustomModeBackupPath(_installPath)
                    : LauncherConfig.CustomModeBackupFile;

                if (!string.IsNullOrWhiteSpace(_installPath) && !string.IsNullOrWhiteSpace(_selectedVersion))
                {
                    string? synced = _updater.SyncCustomModeBackup(_installPath, _selectedVersion);
                    if (!string.IsNullOrEmpty(synced)) backupPath = synced;
                }

                bool isSaved = !string.IsNullOrEmpty(backupPath) && File.Exists(backupPath);

                if (CustomModeStatusIcon != null)
                {
                    if (isSaved)
                    {
                        CustomModeStatusIcon.Data = Avalonia.Media.Geometry.Parse("M9 16.17L4.83 12l-1.42 1.41L9 19 21 7l-1.41-1.41z");
                        CustomModeStatusIcon.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#2ECC71"));
                    }
                    else
                    {
                        CustomModeStatusIcon.Data = Avalonia.Media.Geometry.Parse("M19 6.41L17.59 5 12 10.59 6.41 5 5 6.41 10.59 12 5 17.59 6.41 19 12 13.41 17.59 19 19 17.59 13.41 12z");
                        CustomModeStatusIcon.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#E53935"));
                    }
                }

                string tip;
                if (isSaved && !string.IsNullOrEmpty(backupPath))
                {
                    DateTime lastWrite = File.GetLastWriteTime(backupPath);
                    string dateFormat = I18n.CurrentLang switch
                    {
                        "en" => "MM/dd/yyyy - HH:mm:ss",
                        "ja" or "zh" or "ko" => "yyyy/MM/dd - HH:mm:ss",
                        _ => "dd/MM/yyyy - HH:mm:ss"
                    };
                    string dateTimeStr = lastWrite.ToString(dateFormat);
                    string baseTip = $"{I18n.T("CustomModeSavedTooltip")} ({dateTimeStr})";
                    tip = $"{baseTip}\n{backupPath}";
                }
                else
                {
                    tip = I18n.T("CustomModeNotSavedTooltip");
                }

                if (CustomModeStatusContainer != null)
                {
                    Avalonia.Controls.ToolTip.SetTip(CustomModeStatusContainer, tip);
                    Avalonia.Controls.ToolTip.SetPlacement(CustomModeStatusContainer, Avalonia.Controls.PlacementMode.Top);
                    Avalonia.Controls.ToolTip.SetVerticalOffset(CustomModeStatusContainer, -8);
                    Avalonia.Controls.ToolTip.SetShowDelay(CustomModeStatusContainer, 200);
                }
                if (CustomModeStatusIcon != null)
                {
                    Avalonia.Controls.ToolTip.SetTip(CustomModeStatusIcon, tip);
                }
            }
            catch
            {
                if (CustomModeStatusIcon != null)
                {
                    CustomModeStatusIcon.Data = Avalonia.Media.Geometry.Parse("M19 6.41L17.59 5 12 10.59 6.41 5 5 6.41 10.59 12 5 17.59 6.41 19 12 13.41 17.59 19 19 17.59 13.41 12z");
                    CustomModeStatusIcon.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#E53935"));
                }
                string fallbackTip = I18n.T("CustomModeNotSavedTooltip");
                if (CustomModeStatusContainer != null)
                {
                    Avalonia.Controls.ToolTip.SetTip(CustomModeStatusContainer, fallbackTip);
                    Avalonia.Controls.ToolTip.SetPlacement(CustomModeStatusContainer, Avalonia.Controls.PlacementMode.Top);
                    Avalonia.Controls.ToolTip.SetVerticalOffset(CustomModeStatusContainer, -8);
                    Avalonia.Controls.ToolTip.SetShowDelay(CustomModeStatusContainer, 200);
                }
                if (CustomModeStatusIcon != null)
                {
                    Avalonia.Controls.ToolTip.SetTip(CustomModeStatusIcon, fallbackTip);
                }
            }
        }

        private async Task StartDownloadOrUpdateAsync(bool isUpdate, bool restoreBackup = false)
        {
            if (_downloadCancellation != null) return;
            if (_operationDepth > 0 || _isPlaying) return;
            await CommitPathTextBoxAsync();
            var cancellation = new CancellationTokenSource();
            _downloadCancellation = cancellation;
            BeginOperation();
            bool succeeded = false;
            bool cancelled = false;
            string? backupZip = null;
            string? poseModBackupDir = null;
            try
            {
                if (_isCheckingUpdates && !_backgroundUpdateTask.IsCompleted)
                {
                    StatusText.Text = I18n.T("Checking");
                    try { await _backgroundUpdateTask; } catch { }
                }
                cancellation.Token.ThrowIfCancellationRequested();

                if (isUpdate)
                {
                    string? gameDir = _updater.GetGameDirectory(_installPath, _selectedVersion);
                    if (!string.IsNullOrEmpty(gameDir) && Directory.Exists(gameDir))
                    {
                        if (_updater.VersionSupportsCustomMode(_installPath, _selectedVersion) && _updater.HasCustomModeFiles(gameDir))
                        {
                            AppLogger.Log("Update", $"Backing up Custom Mode from '{gameDir}' prior to update...");
                            backupZip = _updater.BackupCustomMode(_installPath, gameDir);
                            if (string.IsNullOrEmpty(backupZip) || !File.Exists(backupZip))
                            {
                                AppLogger.Error("Update", "Backup of Custom Mode failed prior to update; aborting to preserve existing installation.");
                                ShowOperationError(I18n.T("BackupFailed") ?? "Backup failed prior to update. Aborted to protect your data.");
                                return;
                            }
                        }

                        if (GameUpdater.HasPoseModFiles(gameDir))
                        {
                            AppLogger.Log("Update", $"Backing up PoseMod from '{gameDir}' prior to update...");
                            poseModBackupDir = _updater.BackupPoseMod(gameDir);
                        }

                        AppLogger.Log("Update", $"Deleting previous installation at '{gameDir}' to free disk space...");
                        _updater.DeleteVersion(_installPath, _selectedVersion, out string delError);
                        if (!string.IsNullOrEmpty(delError))
                        {
                            AppLogger.Warning("Update", $"Warning deleting old version: {delError}");
                        }
                    }
                }
                else if (restoreBackup)
                {
                    backupZip = GameUpdater.GetCustomModeBackupPath(_installPath);
                    if (LauncherConfig.HasPoseModBackup)
                    {
                        poseModBackupDir = LauncherConfig.PoseModBackupDir;
                    }
                }

                cancellation.Token.ThrowIfCancellationRequested();
                StorageSpace.EnsureAvailable(_installPath, 4 * StorageSpace.GiB);
                DownloadProgressBar.IsVisible = true;
                DownloadProgressBar.Value = 0;
                if (!await _updater.CheckAndDownloadUpdateAsync(_installPath, _selectedVersion, cancellation.Token))
                {
                    ShowOperationError(_updater.LastError ?? I18n.T("ErrLink"));
                    return;
                }

                if (_selectedVersion == "Latest") _hasUpdateAvailable = false;

                string effectiveBackupZip = (!string.IsNullOrEmpty(backupZip) && File.Exists(backupZip))
                    ? backupZip
                    : GameUpdater.GetCustomModeBackupPath(_installPath);

                if (!string.IsNullOrEmpty(effectiveBackupZip) && File.Exists(effectiveBackupZip) && _updater.VersionSupportsCustomMode(_installPath, _selectedVersion))
                {
                    _updater.GetInstalledVersionsMap(_installPath, forceRefresh: true);
                    string? newGameDir = _updater.GetGameDirectory(_installPath, _selectedVersion);
                    if (!string.IsNullOrEmpty(newGameDir) && Directory.Exists(newGameDir))
                    {
                        AppLogger.Log("Update", $"Restoring Custom Mode into '{newGameDir}' from '{effectiveBackupZip}'...");
                        _updater.RestoreCustomMode(_installPath, newGameDir, effectiveBackupZip);
                    }
                }

                string? effectivePoseModBackup = (!string.IsNullOrEmpty(poseModBackupDir) && Directory.Exists(poseModBackupDir))
                    ? poseModBackupDir
                    : (LauncherConfig.HasPoseModBackup ? LauncherConfig.PoseModBackupDir : null);

                if (!string.IsNullOrEmpty(effectivePoseModBackup) && Directory.Exists(effectivePoseModBackup))
                {
                    _updater.GetInstalledVersionsMap(_installPath, forceRefresh: true);
                    string? newGameDir = _updater.GetGameDirectory(_installPath, _selectedVersion);
                    if (!string.IsNullOrEmpty(newGameDir) && Directory.Exists(newGameDir))
                    {
                        AppLogger.Log("Update", $"Restoring PoseMod into '{newGameDir}' from '{effectivePoseModBackup}'...");
                        _updater.RestorePoseMod(newGameDir, effectivePoseModBackup);
                    }
                }

                if (OperatingSystem.IsMacOS() && LinuxRunnerComboBox.SelectedItem is LinuxRunner runner && runner.NeedsDownload)
                {
                    _downloadCancellation = null;
                    CheckInstallState();
                    StatusText.Text = I18n.T("NextDLRunner");
                    if (!await StartMacRunnerDownloadAsync(runner, launchAfter: false)) return;
                }
                succeeded = true;
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                AppLogger.Log("Download", $"Download cancelled for version '{_selectedVersion}'.");
            }
            catch (Exception ex)
            {
                AppLogger.Error("Install", $"Update action failed for version '{_selectedVersion}'.", ex);
                ShowOperationError(ex.Message);
            }
            finally
            {
                if (ReferenceEquals(_downloadCancellation, cancellation))
                    _downloadCancellation = null;
                cancellation.Dispose();
                EndOperation();
                UpdateTexts();
                UpdateCustomModeIndicator();
            }

            if (cancelled)
            {
                _operationError = null;
                StatusText.Text = I18n.T("DownloadCancelled");
                SetStatusStyle("Default");
            }
            else if (succeeded)
            {
                StatusText.Text = I18n.T("AllDoneReady");
                SetStatusStyle("Updated");
                if (!ShortcutExists()) ShortcutModal.IsVisible = true;
            }
        }

        private void UpdateCloseOnLaunchUI()
        {
            switch (_closeOnLaunchMode)
            {
                case 1: // Reopen on game exit
                    CloseOnLaunchHeart.Data = Avalonia.Media.Geometry.Parse("M12 21.35l-1.45-1.32C5.4 15.36 2 12.28 2 8.5 2 5.42 4.42 3 7.5 3c1.74 0 3.41.81 4.5 2.09C13.09 3.81 14.76 3 16.5 3 19.58 3 22 5.42 22 8.5c0 3.78-3.4 6.86-8.55 11.54L12 21.35z");
                    CloseOnLaunchHeart.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#FF3B7C"));
                    CloseOnLaunchText.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#FF3B7C"));
                    CloseOnLaunchText.Text = I18n.T("CloseOnLaunchReopen");
                    Avalonia.Controls.ToolTip.SetTip(CloseOnLaunchButton, I18n.T("CloseOnLaunchReopen"));
                    break;

                default: // Keep launcher open
                    CloseOnLaunchHeart.Data = Avalonia.Media.Geometry.Parse("M16.5 3c-1.74 0-3.41.81-4.5 2.09C10.91 3.81 9.24 3 7.5 3 4.42 3 2 5.42 2 8.5c0 3.78 3.4 6.86 8.55 11.54L12 21.35l1.45-1.32C18.6 15.36 22 12.28 22 8.5 22 5.42 19.58 3 16.5 3zm-4.4 15.55l-.1.1-.1-.1C7.14 14.24 4 11.39 4 8.5 4 6.5 5.5 5 7.5 5c1.54 0 3.04.99 3.57 2.36h1.87C13.46 5.99 14.96 5 16.5 5c2 0 3.5 1.5 3.5 3.5 0 2.89-3.14 5.74-7.9 10.05z");
                    CloseOnLaunchHeart.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#8C8886"));
                    CloseOnLaunchText.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#504845"));
                    CloseOnLaunchText.Text = I18n.T("CloseOnLaunchKeepOpen");
                    Avalonia.Controls.ToolTip.SetTip(CloseOnLaunchButton, I18n.T("CloseOnLaunchKeepOpen"));
                    break;
            }
        }

        private void CloseOnLaunchButton_Click(object? sender, RoutedEventArgs e)
        {
            _closeOnLaunchMode = (_closeOnLaunchMode + 1) % 2;
            LauncherConfig.CloseOnLaunchMode = _closeOnLaunchMode;
            UpdateCloseOnLaunchUI();
        }

        private async void LatestPostButton_Click(object? sender, RoutedEventArgs e)
        {
            try
            {
                string? cachedUrl = LauncherConfig.LatestPostUrl;
                if (!string.IsNullOrEmpty(cachedUrl) && Uri.TryCreate(cachedUrl, UriKind.Absolute, out _))
                {
                    UpdateNotificationService.OpenBrowser(cachedUrl);
                    _ = Task.Run(async () => await UpdateNotificationService.FetchLatestPostUrlAsync());
                    return;
                }

                string postUrl = await Task.Run(async () => await UpdateNotificationService.FetchLatestPostUrlAsync());
                UpdateNotificationService.OpenBrowser(postUrl);
            }
            catch (Exception ex)
            {
                AppLogger.Warning("LatestPost", "Failed to open latest post link.", ex);
                UpdateNotificationService.OpenBrowser(UpdateNotificationService.BlogUrl);
            }
        }

        private void YouTubePrimary_Click(object? sender, RoutedEventArgs e)
        {
            try
            {
                UpdateNotificationService.OpenBrowser("https://www.youtube.com/@YandereDev");
            }
            catch (Exception ex)
            {
                AppLogger.Warning("YouTube", "Could not open primary YouTube channel.", ex);
            }
        }

        private void YouTubeSecondary_Click(object? sender, RoutedEventArgs e)
        {
            try
            {
                UpdateNotificationService.OpenBrowser("https://www.youtube.com/c/YandereArchive");
            }
            catch (Exception ex)
            {
                AppLogger.Warning("YouTube", "Could not open secondary YouTube channel.", ex);
            }
        }

        private void DiscordButton_Click(object? sender, RoutedEventArgs e)
        {
            try
            {
                UpdateNotificationService.OpenBrowser("https://discord.com/invite/yandere");
            }
            catch (Exception ex)
            {
                AppLogger.Warning("Discord", "Could not open Discord invite link.", ex);
            }
        }

        private void KickButton_Click(object? sender, RoutedEventArgs e)
        {
            try
            {
                UpdateNotificationService.OpenBrowser("https://kick.com/yanderedev/about");
            }
            catch (Exception ex)
            {
                AppLogger.Warning("Kick", "Could not open Kick channel link.", ex);
            }
        }

        private void UpdateAutoUpdateUI()
        {
            if (_autoUpdate)
            {
                AutoUpdateBell.Data = Avalonia.Media.Geometry.Parse("M12 22c1.1 0 2-.9 2-2h-4c0 1.1.9 2 2 2zm6-6v-5c0-3.07-1.63-5.64-4.5-6.32V4c0-.83-.67-1.5-1.5-1.5s-1.5.67-1.5 1.5v.68C7.64 5.36 6 7.92 6 11v5l-2 2v1h16v-1l-2-2z");
                AutoUpdateButton.Classes.Set("active", true);
            }
            else
            {
                AutoUpdateBell.Data = Avalonia.Media.Geometry.Parse("M12 22c1.1 0 2-.9 2-2h-4c0 1.1.9 2 2 2zm6-6v-5c0-3.07-1.63-5.64-4.5-6.32V4c0-.83-.67-1.5-1.5-1.5s-1.5.67-1.5 1.5v.68C7.64 5.36 6 7.92 6 11v5l-2 2v1h16v-1l-2-2zm-2 1H8v-6c0-2.48 1.51-4.5 4-4.5s4 2.02 4 4.5v6z");
                AutoUpdateButton.Classes.Set("active", false);
            }
            Avalonia.Controls.ToolTip.SetTip(AutoUpdateButton, I18n.T("AutoUpdateOption"));
            Avalonia.Controls.ToolTip.SetPlacement(AutoUpdateButton, Avalonia.Controls.PlacementMode.Top);
            Avalonia.Controls.ToolTip.SetVerticalOffset(AutoUpdateButton, -8);
            Avalonia.Controls.ToolTip.SetShowDelay(AutoUpdateButton, 200);
        }

        private void AutoUpdateButton_Click(object? sender, RoutedEventArgs e)
        {
            if (!_autoUpdate)
            {
                NotificationModal.IsVisible = true;
            }
            else
            {
                _autoUpdate = false;
                LauncherConfig.AutoUpdate = false;
                UpdateAutoUpdateUI();
                UpdateNotificationService.UnregisterScheduler();
                AppLogger.Log("AutoUpdate", "Auto-update disabled by user.");
            }
        }

        private void NotificationModalYesButton_Click(object? sender, RoutedEventArgs e)
        {
            NotificationModal.IsVisible = false;
            _autoUpdate = true;
            LauncherConfig.AutoUpdate = true;
            UpdateAutoUpdateUI();
            UpdateNotificationService.RegisterScheduler();
            AppLogger.Log("AutoUpdate", "Auto-update enabled by user.");
        }

        private void NotificationModalNoButton_Click(object? sender, RoutedEventArgs e)
        {
            NotificationModal.IsVisible = false;
        }

        private bool ShortcutExists()
        {
            if (OperatingSystem.IsWindows())
            {
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                return File.Exists(Path.Combine(desktop, "Yandere Simulator.lnk"));
            }
            else if (OperatingSystem.IsMacOS())
            {
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                return Directory.Exists(Path.Combine(desktop, "Yandere Simulator.app"));
            }
            else if (OperatingSystem.IsLinux())
            {
                string appsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "applications");
                return File.Exists(Path.Combine(appsDir, "yandere-simulator.desktop"));
            }
            return false;
        }

        private void ShortcutModalYesButton_Click(object? sender, RoutedEventArgs e)
        {
            ShortcutModal.IsVisible = false;
            try
            {
                CreateDesktopShortcut();
                StatusText.Text = I18n.T("ShortcutCreated");
                SetStatusStyle("Updated");
            }
            catch (Exception ex)
            {
                AppLogger.Error("Shortcut", "Could not create the desktop shortcut.", ex);
                StatusText.Text = I18n.T("ShortcutError") + ex.Message;
                SetStatusStyle("Default");
            }
        }

        private void ShortcutModalNoButton_Click(object? sender, RoutedEventArgs e)
        {
            ShortcutModal.IsVisible = false;
        }

        private void PermissionDeniedModalSettingsButton_Click(object? sender, RoutedEventArgs e)
        {
            PermissionDeniedModal.IsVisible = false;
            try
            {
                if (OperatingSystem.IsMacOS())
                {
                    Process.Start("open", "x-apple.systempreferences:com.apple.preference.security?Privacy_FilesAndFolders");
                }
            }
            catch { }
        }

        private void PermissionDeniedModalCloseButton_Click(object? sender, RoutedEventArgs e)
        {
            PermissionDeniedModal.IsVisible = false;
        }

        private void CreateDesktopShortcut()
        {
            string? gameDir = _updater.GetGameDirectory(_installPath, _selectedVersion) ?? _installPath;
            string? gameExe = _updater.FindExecutable(gameDir);

            if (gameExe == null)
            {
                throw new FileNotFoundException(I18n.T("MissingExe"));
            }

            if (OperatingSystem.IsWindows())
            {
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                string lnkPath = Path.Combine(desktop, "Yandere Simulator.lnk");
                string workingDir = Path.GetDirectoryName(gameExe) ?? gameDir;

                string psScript = $@"
$ws = New-Object -ComObject WScript.Shell;
$sc = $ws.CreateShortcut('{lnkPath.Replace("'", "''")}');
$sc.TargetPath = '{gameExe.Replace("'", "''")}';
$sc.WorkingDirectory = '{workingDir.Replace("'", "''")}';
$sc.IconLocation = '{gameExe.Replace("'", "''")},0';
$sc.Description = 'Yandere Simulator';
$sc.Save()";

                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "powershell",
                    ArgumentList = { "-NoProfile", "-Command", psScript },
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                var proc = System.Diagnostics.Process.Start(psi);
                proc?.WaitForExit();
            }
            else if (OperatingSystem.IsLinux())
            {
                var runner = LinuxRunnerComboBox.SelectedItem as LinuxRunner;
                string prefixPath = GameUpdater.GetPrefixPath(_installPath, runner);
                string appsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "applications");
                Directory.CreateDirectory(appsDir);
                string desktopFile = Path.Combine(appsDir, "yandere-simulator.desktop");
                string workingDir = Path.GetDirectoryName(gameExe) ?? gameDir;
                string wineCmd = EscapeDesktopEntryArgument(runner?.Path ?? "wine");

                string iconPath = Path.Combine(_installPath, "logo.png");
                if (!File.Exists(iconPath))
                {
                    try
                    {
                        if (Avalonia.Platform.AssetLoader.Exists(new Uri("avares://YSLauncher/Assets/logo.png")))
                        {
                            using var s = Avalonia.Platform.AssetLoader.Open(new Uri("avares://YSLauncher/Assets/logo.png"));
                            using var fs = File.Create(iconPath);
                            s.CopyTo(fs);
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Warning("Shortcut", $"Could not export the shortcut icon to '{iconPath}'.", ex);
                    }
                }
                string iconSpec = File.Exists(iconPath) ? iconPath : "applications-games";

                string execCommand;
                if (runner != null && runner.IsProton)
                {
                    execCommand = $"env {EscapeDesktopEntryArgument($"STEAM_COMPAT_DATA_PATH={prefixPath}")} {EscapeDesktopEntryArgument($"STEAM_COMPAT_CLIENT_INSTALL_PATH={Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)}")} {wineCmd} run {EscapeDesktopEntryArgument(gameExe)}";
                }
                else
                {
                    execCommand = $"env {EscapeDesktopEntryArgument($"WINEPREFIX={prefixPath}")} {wineCmd} {EscapeDesktopEntryArgument(gameExe)}";
                }

                string content = $"""
[Desktop Entry]
Type=Application
Name=Yandere Simulator
Exec={execCommand}
Path={workingDir}
Icon={iconSpec}
Terminal=false
Categories=Game;
""";
                File.WriteAllText(desktopFile, content);

                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "chmod",
                        ArgumentList = { "+x", desktopFile },
                        UseShellExecute = false,
                        CreateNoWindow = true
                    })?.WaitForExit();
                }
                catch (Exception ex)
                {
                    AppLogger.Warning("Shortcut", $"Could not mark '{desktopFile}' as executable.", ex);
                }
            }
            else if (OperatingSystem.IsMacOS())
            {
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                string workingDir = Path.GetDirectoryName(gameExe) ?? gameDir;
                var runner = LinuxRunnerComboBox.SelectedItem as LinuxRunner;
                string wineCmd = EscapeShellArgument(runner?.Path ?? "wine");
                string prefixPath = GameUpdater.GetPrefixPath(_installPath, runner);

                string appPath = Path.Combine(desktop, "Yandere Simulator.app");
                string macOsDir = Path.Combine(appPath, "Contents", "MacOS");
                string resourcesDir = Path.Combine(appPath, "Contents", "Resources");
                Directory.CreateDirectory(macOsDir);
                Directory.CreateDirectory(resourcesDir);

                string destIcon = Path.Combine(resourcesDir, "icon.icns");
                bool iconCopied = false;

                string[] candidateIcons = new[]
                {
                    Path.Combine(AppContext.BaseDirectory, "..", "Resources", "icon.icns"),
                    Path.Combine(AppContext.BaseDirectory, "Assets", "icon.icns"),
                    Path.Combine(AppContext.BaseDirectory, "icon.icns"),
                    Path.Combine(Directory.GetCurrentDirectory(), "Assets", "icon.icns"),
                    Path.Combine(Directory.GetCurrentDirectory(), "Assets", "icon_official.icns")
                };

                foreach (var candidate in candidateIcons)
                {
                    if (File.Exists(candidate))
                    {
                        try
                        {
                            File.Copy(candidate, destIcon, true);
                            iconCopied = true;
                            break;
                        }
                        catch (Exception ex)
                        {
                            AppLogger.Warning("Shortcut", $"Could not copy icon '{candidate}'.", ex);
                        }
                    }
                }

                if (!iconCopied)
                {
                    try
                    {
                        Uri? iconUri = null;
                        if (Avalonia.Platform.AssetLoader.Exists(new Uri("avares://YSLauncher/Assets/icon_official.icns")))
                            iconUri = new Uri("avares://YSLauncher/Assets/icon_official.icns");
                        else if (Avalonia.Platform.AssetLoader.Exists(new Uri("avares://YSLauncher/Assets/icon.icns")))
                            iconUri = new Uri("avares://YSLauncher/Assets/icon.icns");

                        if (iconUri != null)
                        {
                            using var stream = Avalonia.Platform.AssetLoader.Open(iconUri);
                            using var fileStream = File.Create(destIcon);
                            stream.CopyTo(fileStream);
                            iconCopied = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Warning("Shortcut", "Could not export the bundled macOS icon.", ex);
                    }
                }

                string plist = """
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleExecutable</key>
    <string>YandereSimulator</string>
    <key>CFBundleIconFile</key>
    <string>icon.icns</string>
    <key>CFBundleIdentifier</key>
    <string>com.yanderesimulator.game</string>
    <key>CFBundleName</key>
    <string>Yandere Simulator</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleShortVersionString</key>
    <string>1.0</string>
    <key>LSMinimumSystemVersion</key>
    <string>10.15</string>
</dict>
</plist>
""";
                File.WriteAllText(Path.Combine(appPath, "Contents", "Info.plist"), plist);

                var runnerRoot = runner != null ? LinuxEngine.GetMacRunnerRoot(runner.Path, _installPath, runner.TargetFolderName) : null;
                var libSearchPaths = runner != null 
                    ? LinuxEngine.GetMacLibraryPaths(runner.Path, _installPath, runner.TargetFolderName)
                    : new List<string>();

                string libExport = libSearchPaths.Any() ? $"export DYLD_FALLBACK_LIBRARY_PATH={EscapeShellArgument(string.Join(":", libSearchPaths))}" : "";

                string? d3dMetalFramework = LinuxEngine.FindMacD3DMetalFramework(runnerRoot);
                bool hasD3DMetal = !string.IsNullOrEmpty(d3dMetalFramework);
                string frameworkExport = hasD3DMetal ? $"export DYLD_FRAMEWORK_PATH={EscapeShellArgument(Path.GetDirectoryName(d3dMetalFramework)!)}" : "";

                string gameD3D11 = Path.Combine(workingDir, "d3d11.dll");
                string dxvkD3D11 = Path.Combine(prefixPath, "drive_c", "windows", "system32", "d3d11.dll");
                string runnerD3D11 = runnerRoot != null ? Path.Combine(runnerRoot, "d3d11.dll") : "";

                string overrideExport = "";
                if (hasD3DMetal)
                {
                    string system32Dxgi = Path.Combine(prefixPath, "drive_c", "windows", "system32", "dxgi.dll");
                    string gameDxgi = Path.Combine(workingDir, "dxgi.dll");
                    overrideExport = (File.Exists(system32Dxgi) || File.Exists(gameDxgi))
                        ? "export WINEDLLOVERRIDES=\"d3d11=n;dxgi=n\""
                        : "export WINEDLLOVERRIDES=\"d3d11=n,b;d3d10core=n,b\"";
                }
                else if (File.Exists(gameD3D11) || File.Exists(dxvkD3D11) || File.Exists(runnerD3D11))
                {
                    overrideExport = "export WINEDLLOVERRIDES=\"d3d11=n,b;d3d10core=n,b\"";
                }

                string execScript = $"""
#!/bin/bash
export WINEPREFIX={EscapeShellArgument(prefixPath)}
export WINEDEBUG="-all"
{libExport}
{frameworkExport}
{overrideExport}
cd {EscapeShellArgument(workingDir)}
exec {wineCmd} {EscapeShellArgument(gameExe)}
""";
                string execPath = Path.Combine(macOsDir, "YandereSimulator");
                File.WriteAllText(execPath, execScript);

                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "chmod",
                        ArgumentList = { "+x", execPath },
                        UseShellExecute = false,
                        CreateNoWindow = true
                    })?.WaitForExit();
                }
                catch (Exception ex)
                {
                    AppLogger.Warning("Shortcut", $"Could not mark '{execPath}' as executable.", ex);
                }

                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "touch",
                        ArgumentList = { appPath },
                        UseShellExecute = false,
                        CreateNoWindow = true
                    })?.WaitForExit();
                }
                catch (Exception ex)
                {
                    AppLogger.Warning("Shortcut", $"Could not refresh Finder metadata for '{appPath}'.", ex);
                }
            }
        }

        private static string EscapeDesktopEntryArgument(string value) =>
            $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("`", "\\`").Replace("$", "\\$")}\"";

        private static string EscapeShellArgument(string value) =>
            $"'{value.Replace("'", "'\"'\"'")}'";

        private bool CanLaunchPoseMod()
        {
            if (string.IsNullOrWhiteSpace(_installPath)) return false;
            string? gameDir = _updater.GetGameDirectory(_installPath, _selectedVersion);
            if (string.IsNullOrEmpty(gameDir) && _selectedVersion == "Latest")
                gameDir = _installPath;
            return !string.IsNullOrEmpty(gameDir) && GameUpdater.HasPoseMod(gameDir);
        }

        private void SetPoseModShiftState(bool isHeld)
        {
            if (_isPoseModShiftHeld == isHeld) return;
            _isPoseModShiftHeld = isHeld;

            if (_isPlaying || _isStopping || _operationDepth > 0) return;

            if (_isPoseModShiftHeld)
            {
                bool hasPoseMod = CanLaunchPoseMod();
                PlayText.Text = "POSEMOD";

                if (hasPoseMod)
                {
                    PlayButton.Classes.Set("posemod-missing", false);
                    PlayButton.Classes.Set("posemod", true);
                    PlayButton.Background = new SolidColorBrush(Color.Parse("#E53935"));
                    Avalonia.Controls.ToolTip.SetTip(PlayButton, I18n.T("PoseModSavedTooltip"));
                }
                else
                {
                    PlayButton.Classes.Set("posemod", false);
                    PlayButton.Classes.Set("posemod-missing", true);
                    PlayButton.Background = new SolidColorBrush(Color.Parse("#7E0316"));
                    Avalonia.Controls.ToolTip.SetTip(PlayButton, I18n.T("PoseModNotFound"));
                }

                Avalonia.Controls.ToolTip.SetPlacement(PlayButton, Avalonia.Controls.PlacementMode.Top);
                Avalonia.Controls.ToolTip.SetVerticalOffset(PlayButton, -8);
                Avalonia.Controls.ToolTip.SetShowDelay(PlayButton, 100);
                if (PlayButton.IsPointerOver)
                {
                    Avalonia.Controls.ToolTip.SetIsOpen(PlayButton, true);
                }
            }
            else
            {
                PlayButton.Classes.Set("posemod", false);
                PlayButton.Classes.Set("posemod-missing", false);
                PlayButton.Background = new SolidColorBrush(Color.Parse("#E88CA6"));
                PlayText.Text = I18n.T("Play");
                Avalonia.Controls.ToolTip.SetIsOpen(PlayButton, false);
                Avalonia.Controls.ToolTip.SetTip(PlayButton, null);
            }
        }

        private void Window_PreviewKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.LeftShift || e.Key == Key.RightShift)
            {
                SetPoseModShiftState(true);
            }
        }

        private void Window_PreviewKeyUp(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.LeftShift || e.Key == Key.RightShift)
            {
                SetPoseModShiftState(false);
            }
        }

        private async void PlayButton_Click(object? sender, RoutedEventArgs e)
        {
            if (_operationDepth > 0 || _isStopping) return;
            if (_isPlaying)
            {
                AppLogger.Log("Play", "Game is currently playing. Stopping running game...");
                await StopRunningGameAsync();
                return;
            }

            if (_isPoseModShiftHeld && !CanLaunchPoseMod())
            {
                ShowOperationError(I18n.T("PoseModNotFound"));
                return;
            }

            bool launchPoseMod = _isPoseModShiftHeld && CanLaunchPoseMod();
            SetPoseModShiftState(false);
            await LaunchGameCoreAsync(launchPoseMod);
        }

        private async Task LaunchGameCoreAsync(bool launchPoseMod = false)
        {
            await CommitPathTextBoxAsync();
            _operationError = null;
            AppLogger.Log("Play", $"Launch requested for version='{_selectedVersion}', path='{_installPath}', posemod={launchPoseMod}");

            try
            {
                if ((OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) && LinuxRunnerComboBox.SelectedItem is LinuxRunner runner)
                {
                    AppLogger.Log("Play", $"Using runner: '{runner.Name}', path='{runner.Path}', needsDownload={runner.NeedsDownload}");

                    if (OperatingSystem.IsMacOS() && (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 || RuntimeInformation.OSArchitecture == Architecture.Arm64))
                    {
                        if (!LinuxEngine.IsRosettaInstalled())
                        {
                            AppLogger.Log("Play", "Rosetta 2 is required on Apple Silicon. Prompting installation...");
                            StatusText.Text = I18n.T("InstallingRosetta");
                            bool rosettaOk = await LinuxEngine.InstallRosettaAsync();
                            if (!rosettaOk)
                            {
                                ShowOperationError(I18n.T("RosettaError"));
                                return;
                            }
                        }
                    }

                    if (runner.NeedsDownload)
                    {
                        if (OperatingSystem.IsMacOS()) await StartMacRunnerDownloadAsync(runner, launchAfter: true);
                        else ProtonModal.IsVisible = true;
                        return;
                    }

                    HandleGameLaunch(_updater.LaunchGameProcess(_installPath, _selectedVersion, runner, launchPoseMod), runner, isPoseMod: launchPoseMod);
                }
                else
                {
                    HandleGameLaunch(_updater.LaunchGameProcess(_installPath, _selectedVersion, null, launchPoseMod), null, isPoseMod: launchPoseMod);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("Play", "Launch error", ex);
                ShowOperationError(I18n.T("ErrLaunch") + ex.Message);
            }
            finally
            {
                CheckInstallState();
            }
        }

        private bool HandleGameLaunch(Process? proc, LinuxRunner? runner = null, bool isPoseMod = false)
        {
            if (proc == null)
            {
                AppLogger.Error("Play", $"HandleGameLaunch: Process is null! Error: {_updater.LastError}");
                ShowOperationError(_updater.LastError ?? I18n.T("ErrLaunch"));
                CheckInstallState();
                return false;
            }
            AppLogger.Log("Play", $"HandleGameLaunch: Process started. PID={proc.Id}, isPoseMod={isPoseMod}");

            string launchedInstallPath = _installPath;
            var session = runner == null
                ? new GameProcessSession(proc, isPoseMod: isPoseMod)
                : new GameProcessSession(proc, cancellationToken =>
                    _updater.StopCompatibilityProcessesAsync(launchedInstallPath, runner, cancellationToken), isPoseMod: isPoseMod);
            _runningGameProcess = session;
            _isPlaying = true;
            CheckInstallState();
            _ = TrackGameProcessAsync(session);

            if (_closeOnLaunch)
            {
                _ = CloseAfterGameStartAsync(session);
            }
            return true;
        }

        private async Task CloseAfterGameStartAsync(GameProcessSession session)
        {
            AppLogger.Log("Play", $"CloseOnLaunch: Waiting for game startup before closing (PID={session.Id})...");

            const int waitSeconds = 8;
            for (int i = 0; i < waitSeconds; i++)
            {
                await Task.Delay(1000);

                if (!_isPlaying || session.Completion.IsCompleted)
                {
                    AppLogger.Log("Play", "CloseOnLaunch: Aborting close because game exited or stopped during delay.");
                    return;
                }
            }

            if (_isPlaying && !session.Completion.IsCompleted)
            {
                int targetPid = session.Id;
                if (_closeOnLaunchMode == 1)
                {
                    AppLogger.Log("Play", $"CloseOnLaunch (reopen mode): Starting background watcher for PID={targetPid} before closing...");
                    try
                    {
                        string currentExe = Environment.ProcessPath ?? "";
                        if (!string.IsNullOrEmpty(currentExe) && File.Exists(currentExe))
                        {
                            var psi = new ProcessStartInfo
                            {
                                FileName = currentExe,
                                Arguments = $"--wait-and-relaunch {targetPid}",
                                UseShellExecute = false,
                                CreateNoWindow = true
                            };
                            Process.Start(psi);
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Error("Play", "Failed to start background watcher for relaunch", ex);
                    }
                }

                AppLogger.Log("Play", $"CloseOnLaunch (mode={_closeOnLaunchMode}): Game started successfully. Closing launcher now.");
                await Dispatcher.UIThread.InvokeAsync(Close);
            }
        }

        private async Task StopRunningGameAsync()
        {
            var proc = _runningGameProcess;
            if (proc == null || _isStopping) return;
            _isStopping = true;
            CheckInstallState();
            try
            {
                AppLogger.Log("Play", $"Stopping process PID {proc.Id}...");
                await proc.StopAsync();
            }
            catch (Exception ex)
            {
                AppLogger.Error("Play", "Error stopping process", ex);
                if (ReferenceEquals(_runningGameProcess, proc))
                    ShowOperationError(ex.Message);
            }
            finally
            {
                _isStopping = false;
                CheckInstallState();
            }
        }

        private async Task TrackGameProcessAsync(GameProcessSession proc)
        {
            try
            {
                AppLogger.Log("Play", $"Tracking game process PID {proc.Id}...");
                await proc.Completion;
                AppLogger.Log("Play", $"Game process PID {proc.Id} exited with code {proc.ExitCode}");
                if (ReferenceEquals(_runningGameProcess, proc) && !proc.WasStopRequested && proc.ExitCode != 0)
                {
                    if (proc.IsPoseMod && (proc.ExitCode == 202 || proc.ExitCode == 0))
                    {
                        AppLogger.Log("Play", $"PoseMod process exited with handoff code {proc.ExitCode}.");
                    }
                    else
                    {
                        ShowOperationError(I18n.T("ErrLaunch") + $"Exit code: {proc.ExitCode}");
                    }
                }

                try
                {
                    if (!string.IsNullOrWhiteSpace(_installPath) && _updater.VersionSupportsCustomMode(_installPath, _selectedVersion))
                    {
                        string? gameDir = _updater.GetGameDirectory(_installPath, _selectedVersion);
                        if (!string.IsNullOrEmpty(gameDir) && _updater.HasCustomModeFiles(gameDir))
                        {
                            AppLogger.Log("Play", "Auto-backing up Custom Mode after game exit...");
                            _updater.BackupCustomMode(_installPath, gameDir);
                            Avalonia.Threading.Dispatcher.UIThread.Post(UpdateCustomModeIndicator);
                        }
                    }
                }
                catch (Exception backupEx)
                {
                    AppLogger.Error("Play", "Failed auto-backup after game exit", backupEx);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("Play", "Exception in game tracking", ex);
                if (ReferenceEquals(_runningGameProcess, proc)) ShowOperationError(ex.Message);
            }
            finally
            {
                if (ReferenceEquals(_runningGameProcess, proc))
                {
                    _runningGameProcess = null;
                    _isPlaying = false;
                    CheckInstallState();
                    UpdateCustomModeIndicator();
                }
                proc.Dispose();
            }
        }

        private async Task<bool> StartMacRunnerDownloadAsync(LinuxRunner runner, bool launchAfter = true)
        {
            BeginOperation();
            StatusText.Text = string.Format(I18n.T("DLRunner"), runner.DisplayName);
            SetStatusStyle("Default");

            try
            {
                await _backgroundUpdateTask;
                string runnersDir = LinuxEngine.GetRunnersDirectory(_installPath);
                StorageSpace.EnsureAvailable(runnersDir, 2 * StorageSpace.GiB);
                DownloadProgressBar.IsVisible = true;
                DownloadProgressBar.Value = 0;

                Directory.CreateDirectory(runnersDir);

                string targetDir = Path.Combine(runnersDir, runner.TargetFolderName);
                Directory.CreateDirectory(targetDir);

                string tarPath = Path.Combine(runnersDir, $"{runner.TargetFolderName}.tar.xz");

                await _updater.DownloadFileMultiThreadedAsync(runner.DownloadUrl, tarPath, 4, runner.DisplayName);

                if (!string.IsNullOrEmpty(runner.ExpectedSha256))
                {
                    StatusText.Text = I18n.T("VerifyingSha");
                    using var sha = System.Security.Cryptography.SHA256.Create();
                    using var fs = File.OpenRead(tarPath);
                    byte[] hash = sha.ComputeHash(fs);
                    string hashStr = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
                    if (!string.Equals(hashStr, runner.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException(I18n.T("ShaMismatch"));
                    }
                }

                StatusText.Text = string.Format(I18n.T("ExtRunner"), runner.DisplayName);
                StorageSpace.EnsureAvailable(targetDir, 2 * StorageSpace.GiB);
                using var tarProcess = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "tar",
                    ArgumentList = { "-xf", tarPath, "-C", targetDir },
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                if (tarProcess == null) throw new IOException("Could not start tar.");
                await tarProcess.WaitForExitAsync();
                if (tarProcess.ExitCode != 0) throw new IOException($"tar failed (exit {tarProcess.ExitCode}).");

                try
                {
                    File.Delete(tarPath);
                }
                catch (Exception ex)
                {
                    AppLogger.Warning("Runner", $"Could not delete archive '{tarPath}'.", ex);
                }

                LinuxEngine.CleanMacQuarantineAndSign(targetDir);

                StatusText.Text = I18n.T("ConfiguringEngine");
                string prefixPath = GameUpdater.GetPrefixPath(_installPath, runner);
                string? gameDir = _updater.GetGameDirectory(_installPath, _selectedVersion);
                if (!LinuxEngine.DeployMacRuntime(targetDir, prefixPath, gameDir))
                    throw new IOException("Compatibility runtime configuration failed.");

                InitializeLinuxRunners();
                var installedRunner = (LinuxRunnerComboBox.ItemsSource as IEnumerable<LinuxRunner>)?
                    .FirstOrDefault(r => r.TargetFolderName == runner.TargetFolderName && !r.NeedsDownload);

                if (installedRunner == null) throw new IOException("Installed runner executable not found.");
                LinuxRunnerComboBox.SelectedItem = installedRunner;
                if (launchAfter && _updater.IsGameInstalled(_installPath, _selectedVersion))
                    return HandleGameLaunch(_updater.LaunchGameProcess(_installPath, _selectedVersion, installedRunner), installedRunner);
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Error("Runner", $"Could not install macOS runner '{runner.DisplayName}'.", ex);
                ShowOperationError(I18n.T("ErrRunner") + ex.Message);
                return false;
            }
            finally
            {
                EndOperation();
            }
        }

        private void ProtonCancelButton_Click(object? sender, RoutedEventArgs e)
        {
            ProtonModal.IsVisible = false;
        }

        private async void ProtonIsolatedButton_Click(object? sender, RoutedEventArgs e)
        {
            ProtonModal.IsVisible = false;
            await StartProtonDownloadAsync(false);
        }

        private async void ProtonSteamButton_Click(object? sender, RoutedEventArgs e)
        {
            ProtonModal.IsVisible = false;
            await StartProtonDownloadAsync(true);
        }

        private async Task StartProtonDownloadAsync(bool installToSteam)
        {
            if (_operationDepth > 0 || _isPlaying) return;
            BeginOperation();
            try
            {
                await _backgroundUpdateTask;
                StatusText.Text = I18n.T("SearchProton");
                string? url = await LinuxEngine.GetLatestGEProtonUrlAsync();
                if (url == null) throw new IOException(I18n.T("ErrProtonLink"));
                string runnersDir = installToSteam
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "Steam", "compatibilitytools.d")
                    : LinuxEngine.GetRunnersDirectory(_installPath);
                StorageSpace.EnsureAvailable(runnersDir, 2 * StorageSpace.GiB);
                StatusText.Text = I18n.T("DLProton");
                DownloadProgressBar.IsVisible = true;
                DownloadProgressBar.Value = 0;
                Directory.CreateDirectory(runnersDir);
                string tarPath = Path.Combine(runnersDir, "proton.tar.gz");
                await _updater.DownloadFileMultiThreadedAsync(url, tarPath, 4);
                StatusText.Text = I18n.T("ExtProton");
                StorageSpace.EnsureAvailable(runnersDir, 2 * StorageSpace.GiB);
                using var tar = Process.Start(new ProcessStartInfo
                {
                    FileName = "tar",
                    ArgumentList = { "-xzf", tarPath, "-C", runnersDir },
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                if (tar == null) throw new IOException("Could not start tar.");
                await tar.WaitForExitAsync();
                if (tar.ExitCode != 0) throw new IOException($"tar failed (exit {tar.ExitCode}).");
                File.Delete(tarPath);
                InitializeLinuxRunners();
                string releaseFolder = Path.GetFileName(new Uri(url).AbsolutePath).Replace(".tar.gz", "");
                string unqualifiedFolder = releaseFolder.EndsWith("-x86_64", StringComparison.Ordinal)
                    ? releaseFolder[..^7] : releaseFolder;
                var installedRunner = (LinuxRunnerComboBox.ItemsSource as IEnumerable<LinuxRunner>)?
                    .FirstOrDefault(r => r.IsProton && !r.NeedsDownload &&
                        (Path.GetFullPath(r.Path) == Path.GetFullPath(Path.Combine(runnersDir, releaseFolder, "proton")) ||
                         Path.GetFullPath(r.Path) == Path.GetFullPath(Path.Combine(runnersDir, unqualifiedFolder, "proton"))));
                if (installedRunner == null) throw new IOException("Installed Proton executable not found.");
                LinuxRunnerComboBox.SelectedItem = installedRunner;
                HandleGameLaunch(_updater.LaunchGameProcess(_installPath, _selectedVersion, installedRunner), installedRunner);
            }
            catch (Exception ex)
            {
                AppLogger.Error("Runner", "Could not install GE-Proton.", ex);
                ShowOperationError(I18n.T("ErrProton") + ex.Message);
            }
            finally { EndOperation(); }
        }

        private List<DiscoveredGameVersion>? _pendingOrganizeItems;
        private string? _pendingOrganizeTarget;

        private void OrganizeFoldersButton_Click(object? sender, RoutedEventArgs e)
        {
            OrganizePromptModal.IsVisible = true;
        }

        private void OrganizePromptCancelButton_Click(object? sender, RoutedEventArgs e)
        {
            OrganizePromptModal.IsVisible = false;
        }

        private async void OrganizePromptSelectButton_Click(object? sender, RoutedEventArgs e)
        {
            OrganizePromptModal.IsVisible = false;
            await StartOrganizePickerAsync();
        }

        private async void OrganizeOnboardingYesButton_Click(object? sender, RoutedEventArgs e)
        {
            OrganizeOnboardingModal.IsVisible = false;
            await StartOrganizePickerAsync();
        }

        private void OrganizeOnboardingNoButton_Click(object? sender, RoutedEventArgs e)
        {
            OrganizeOnboardingModal.IsVisible = false;
            ShowMainScreen();
        }

        private async Task StartOrganizePickerAsync()
        {
            try
            {
                var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    Title = I18n.T("SelectFolder"),
                    AllowMultiple = false
                });

                if (folders == null || folders.Count == 0) return;

                string? selectedPath = folders[0].TryGetLocalPath();
                if (string.IsNullOrWhiteSpace(selectedPath) || !Directory.Exists(selectedPath)) return;

                AppLogger.Log("Organize", $"Scanning folder for versions to organize: '{selectedPath}'");
                string targetLibrary = _installPath;
                if (string.IsNullOrWhiteSpace(targetLibrary) || !Directory.Exists(targetLibrary))
                {
                    targetLibrary = selectedPath;
                }

                ShowLoadingOverlay(true);
                List<DiscoveredGameVersion> items;
                try
                {
                    items = await Task.Run(() => _updater.ScanForVersionsToOrganize(selectedPath, targetLibrary));
                }
                finally
                {
                    ShowLoadingOverlay(false);
                }

                AppLogger.Log("Organize", $"Scan found {items.Count} items to organize.");

                if (items.Count == 0)
                {
                    StatusText.Text = I18n.T("OrganizeNoneFound");
                    SetStatusStyle("Notice");
                    return;
                }

                _pendingOrganizeItems = items;
                _pendingOrganizeTarget = targetLibrary;

                OrganizePreviewList.Children.Clear();
                foreach (var item in items)
                {
                    AppLogger.Log("Organize", $"Found version: {item.DisplaySummary} (from '{item.SourcePath}' to '{item.TargetSubfolderName}')");
                    var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8 };
                    row.Children.Add(new TextBlock { Text = "-", FontSize = 18, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
                    row.Children.Add(new TextBlock 
                    { 
                        Text = item.DisplaySummary, 
                        FontSize = 18, 
                        Foreground = Brush.Parse("#504845"), 
                        TextWrapping = TextWrapping.Wrap, 
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center 
                    });
                    OrganizePreviewList.Children.Add(row);
                }

                OrganizePreviewModal.IsVisible = true;
            }
            catch (Exception ex)
            {
                AppLogger.Error("Organize", "Error during scan", ex);
                ShowOperationError("Erro ao escanear pastas: " + ex.Message);
            }
        }

        private async void OrganizeConfirmButton_Click(object? sender, RoutedEventArgs e)
        {
            OrganizePreviewModal.IsVisible = false;
            if (_pendingOrganizeItems == null || _pendingOrganizeItems.Count == 0 || string.IsNullOrWhiteSpace(_pendingOrganizeTarget))
                return;

            AppLogger.Log("Organize", $"Executing organize for {_pendingOrganizeItems.Count} items to target '{_pendingOrganizeTarget}'...");
            bool ok = _updater.ExecuteOrganize(_pendingOrganizeItems, _pendingOrganizeTarget, out string error);
            if (ok)
            {
                AppLogger.Log("Organize", "Organize executed successfully!");
                _installPath = _pendingOrganizeTarget;
                _isUpdatingTextProgrammatically = true;
                PathTextBox.Text = _installPath;
                _isUpdatingTextProgrammatically = false;

                LauncherConfig.InstallPath = _installPath;

                await RefreshVersionsForCurrentPathAsync();
                var mostRecent = _updater.FindMostRecentLocalVersion(_installPath);
                if (!string.IsNullOrEmpty(mostRecent))
                {
                    SelectVersionInComboBox(mostRecent);
                }

                StatusText.Text = I18n.T("OrganizeSuccess");
                SetStatusStyle("Success");
                CheckInstallState();
            }
            else
            {
                AppLogger.Error("Organize", $"ExecuteOrganize failed: {error}");
                ShowOperationError("Erro ao organizar versões: " + error);
            }
        }

        private void OrganizeCancelButton_Click(object? sender, RoutedEventArgs e)
        {
            OrganizePreviewModal.IsVisible = false;
        }

        private void Updater_StatusChanged(object? sender, string message)
        {
            Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_operationError != null) return;
                StatusText.Text = message;
                SetStatusStyle("Default");
            });
        }

        private void Updater_ProgressChanged(object? sender, int percentage)
        {
            Dispatcher.UIThread.InvokeAsync(() =>
            {
                DownloadProgressBar.Value = percentage;
            });
        }

        private void Updater_DownloadCompleted(object? sender, EventArgs e)
        {
            Dispatcher.UIThread.InvokeAsync(() =>
            {
                DownloadProgressBar.IsVisible = false;
            });
        }
    }
}
