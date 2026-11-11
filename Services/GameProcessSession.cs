using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace YSLauncher.Services;

internal sealed class GameProcessSession : IDisposable
{
    private Process _process;
    private readonly Func<CancellationToken, Task>? _stopCompatibilityProcesses;
    private readonly bool _isPoseMod;
    private readonly TaskCompletionSource _completionTcs = new();

    public int Id => _process.Id;
    public Task Completion => _completionTcs.Task;
    public int ExitCode { get; private set; }
    public bool WasStopRequested { get; private set; }
    public bool IsPoseMod => _isPoseMod;

    public GameProcessSession(Process process, Func<CancellationToken, Task>? stopCompatibilityProcesses = null, bool isPoseMod = false)
    {
        _process = process;
        _stopCompatibilityProcesses = stopCompatibilityProcesses;
        _isPoseMod = isPoseMod;
        _ = MonitorLifecycleAsync();
    }

    private async Task MonitorLifecycleAsync()
    {
        try
        {
            if (!_isPoseMod)
            {
                await _process.WaitForExitAsync();
                ExitCode = _process.ExitCode;
                _completionTcs.TrySetResult();
                return;
            }

            // PoseMod lifecycle:
            // PoseMod64.exe is an injector that spawns YandereSimulator.exe and then exits with code 202.
            AppLogger.Log("Play", $"PoseMod session started with injector PID {_process.Id}. Monitoring for game process handoff...");

            var injectorExitTask = _process.WaitForExitAsync();
            Process? gameProc = null;

            // Wait up to 20 seconds for YandereSimulator to be detected
            for (int i = 0; i < 40; i++)
            {
                if (WasStopRequested) break;

                gameProc = FindRunningGameProcess(_process.Id);
                if (gameProc != null)
                {
                    AppLogger.Log("Play", $"Detected spawned game process with PID {gameProc.Id} ({gameProc.ProcessName})!");
                    break;
                }

                if (injectorExitTask.IsCompleted)
                {
                    AppLogger.Log("Play", $"PoseMod injector PID {_process.Id} exited with code {_process.ExitCode}. Checking for running game...");
                    // Give it a generous window for YandereSimulator.exe to appear in process table (especially on macOS / Wine startup)
                    for (int j = 0; j < 40; j++)
                    {
                        gameProc = FindRunningGameProcess(_process.Id);
                        if (gameProc != null) break;
                        await Task.Delay(500);
                    }
                    break;
                }

                await Task.Delay(500);
            }

            if (gameProc != null)
            {
                AppLogger.Log("Play", $"PoseMod handoff successful! Switched tracking to game process PID {gameProc.Id}.");
                _process = gameProc;
                ExitCode = 0;

                if (OperatingSystem.IsMacOS())
                {
                    CloseMacPoseModTerminal();
                }

                // Monitor until YandereSimulator exits
                await WaitForProcessExitAsync(gameProc);
                AppLogger.Log("Play", $"Game process PID {gameProc.Id} exited.");
                _completionTcs.TrySetResult();
            }
            else
            {
                // Injector terminated without game running
                await injectorExitTask;
                ExitCode = _process.ExitCode;
                if (ExitCode == 202)
                {
                    // Exit code 202 is PoseMod's standard success code signaling injection complete.
                    AppLogger.Log("Play", $"PoseMod injector completed injection successfully (code 202).");
                    ExitCode = 0;
                }
                else
                {
                    AppLogger.Warning("Play", $"PoseMod injector terminated with code {ExitCode} and no game process was found.");
                }
                _completionTcs.TrySetResult();
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("Play", "Error monitoring game process lifecycle", ex);
            _completionTcs.TrySetResult();
        }
    }

    private static async Task WaitForProcessExitAsync(Process proc)
    {
        int pid = proc.Id;
        while (IsProcessAlive(pid) && !proc.HasExited)
        {
            await Task.Delay(500);
        }
    }

    public static bool IsProcessAlive(int pid)
    {
        if (pid <= 0) return false;

        if (OperatingSystem.IsLinux())
        {
            if (Directory.Exists($"/proc/{pid}")) return true;
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            try
            {
                return kill(pid, 0) == 0;
            }
            catch
            {
                return false;
            }
        }

        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var proc = Process.GetProcessById(pid);
                return !proc.HasExited;
            }
            catch
            {
                return false;
            }
        }

        return false;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int sig);

    private static Process? FindRunningGameProcess(int excludePid)
    {
        try
        {
            int currentAppPid = Environment.ProcessId;

            // Check by standard process name (Windows, Linux, macOS)
            string[] names = { "YandereSimulator", "YandereSimulato" };
            foreach (var name in names)
            {
                var procs = Process.GetProcessesByName(name);
                foreach (var p in procs)
                {
                    if (p.Id != excludePid && p.Id != currentAppPid && !p.HasExited)
                        return p;
                }
            }

            // Scan /proc/[pid]/cmdline (Linux zero-subprocess scan)
            if (OperatingSystem.IsLinux() && Directory.Exists("/proc"))
            {
                foreach (var dir in Directory.GetDirectories("/proc"))
                {
                    string pidStr = Path.GetFileName(dir);
                    if (!int.TryParse(pidStr, out int pid) || pid <= 1 || pid == excludePid || pid == currentAppPid) continue;

                    try
                    {
                        string cmdlinePath = Path.Combine(dir, "cmdline");
                        if (!File.Exists(cmdlinePath)) continue;

                        byte[] bytes = File.ReadAllBytes(cmdlinePath);
                        if (bytes.Length == 0) continue;

                        string cmdline = Encoding.UTF8.GetString(bytes).Replace('\0', ' ');
                        if (cmdline.Contains("YandereSimulator.exe", StringComparison.OrdinalIgnoreCase) &&
                            !cmdline.Contains("PoseMod", StringComparison.OrdinalIgnoreCase))
                        {
                            var proc = Process.GetProcessById(pid);
                            if (!proc.HasExited) return proc;
                        }
                    }
                    catch { }
                }
            }

            // macOS and Unix fallback (pgrep -f)
            if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "pgrep",
                        ArgumentList = { "-f", "YandereSimulator" },
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using var pgrepProc = Process.Start(psi);
                    if (pgrepProc != null)
                    {
                        string output = pgrepProc.StandardOutput.ReadToEnd();
                        pgrepProc.WaitForExit(1000);
                        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        {
                            if (int.TryParse(line, out int matchedPid) && matchedPid != excludePid && matchedPid != currentAppPid)
                            {
                                try
                                {
                                    if (OperatingSystem.IsMacOS())
                                    {
                                        string cmdline = GetMacProcessCommandLine(matchedPid);
                                        if (cmdline.Contains("PoseMod", StringComparison.OrdinalIgnoreCase))
                                        {
                                            continue;
                                        }
                                    }

                                    var proc = Process.GetProcessById(matchedPid);
                                    if (!proc.HasExited) return proc;
                                }
                                catch { }
                            }
                        }
                    }
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warning("Process", "Error while scanning for game process", ex);
        }

        return null;
    }

    private static string GetMacProcessCommandLine(int pid)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "ps",
                ArgumentList = { "-p", pid.ToString(), "-o", "command=" },
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p != null)
            {
                string output = p.StandardOutput.ReadToEnd();
                p.WaitForExit(500);
                return output;
            }
        }
        catch { }
        return "";
    }

    public static void CloseMacPoseModTerminal()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "osascript",
                ArgumentList = { "-e", "tell application \"Terminal\" to close (every window whose name contains \"PoseMod\")" },
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(2000);
        }
        catch { }
    }

    public async Task StopAsync()
    {
        WasStopRequested = true;

        if (_stopCompatibilityProcesses != null)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            try
            {
                await _stopCompatibilityProcesses(timeout.Token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AppLogger.Warning("Play", "Compatibility session shutdown failed. Falling back to process-tree termination.", ex);
            }
            catch (OperationCanceledException)
            {
                AppLogger.Warning("Play", "Compatibility session shutdown timed out. Falling back to process-tree termination.");
            }
        }

        if (!Completion.IsCompleted && !_process.HasExited)
        {
            try { _process.Kill(entireProcessTree: true); } catch { }
        }

        await Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    public void Dispose() => _process.Dispose();
}
