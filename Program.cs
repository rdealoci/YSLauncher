using Avalonia;
using System;
using System.Linq;
using YSLauncher.Services;

namespace YSLauncher;

public class Program
{
    public static bool AutoUpdateRequested { get; set; } = false;

    [STAThread]
    public static void Main(string[] args)
    {
        int waitIdx = Array.IndexOf(args, "--wait-and-relaunch");
        if (waitIdx >= 0 && waitIdx + 1 < args.Length && int.TryParse(args[waitIdx + 1], out int targetPid))
        {
            WaitAndRelaunch(targetPid);
            return;
        }

        if (args.Contains("--check-updates"))
        {
            UpdateNotificationService.RunHeadlessCheckAsync().GetAwaiter().GetResult();
            return;
        }

        if (args.Contains("--test-notify"))
        {
            I18n.AutoDetect();
            UpdateNotificationService.NotifyUpdateAvailable();
            return;
        }

        if (args.Contains("--auto-update") || UpdateNotificationService.HasPendingUpdate())
        {
            AutoUpdateRequested = true;
            UpdateNotificationService.ConsumePendingUpdate();
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static void WaitAndRelaunch(int targetPid)
    {
        try
        {
            while (GameProcessSession.IsProcessAlive(targetPid))
            {
                System.Threading.Thread.Sleep(500);
            }
        }
        catch
        {
            // The target process has already terminated.
        }

        System.Threading.Thread.Sleep(1000);

        try
        {
            string currentExe = Environment.ProcessPath ?? "";
            if (!string.IsNullOrEmpty(currentExe) && System.IO.File.Exists(currentExe))
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = currentExe,
                    UseShellExecute = true
                };
                System.Diagnostics.Process.Start(psi);
            }
        }
        catch { }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
