using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace YSLauncher.Services;
    public static class AppLogger
    {
        private static readonly object _lock = new();
        private static readonly string _logDirectory = LauncherConfig.ConfigDir;
        private static readonly string _logFile = LauncherConfig.LogFile;
        private static readonly string _previousLogFile = LauncherConfig.LogPreviousFile;
        private const long MaxLogBytes = 2 * 1024 * 1024;

        public static string LogFilePath => _logFile;

        public static void Log(string tag, string message)
        {
            Write("INFO", tag, message, null);
        }

        public static void Warning(string tag, string message, Exception? ex = null)
        {
            Write("WARN", tag, message, ex);
        }

        public static void Error(string tag, string message, Exception? ex = null)
        {
            Write("ERROR", tag, message, ex);
        }

        private static void Write(string level, string tag, string message, Exception? ex)
        {
            string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] [{tag}] {message}" +
                (ex == null ? string.Empty : $"{Environment.NewLine}{ex}");

            lock (_lock)
            {
                try
                {
                    Directory.CreateDirectory(_logDirectory);
                    if (File.Exists(_logFile) && new FileInfo(_logFile).Length >= MaxLogBytes)
                        File.Move(_logFile, _previousLogFile, true);
                    File.AppendAllText(_logFile, line + Environment.NewLine, Encoding.UTF8);
                }
                catch (Exception logException)
                {
                    Debug.WriteLine($"Logger file failure: {logException}");
                }

                try
                {
                    if (level == "ERROR")
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.Error.WriteLine(line);
                        Console.ResetColor();
                    }
                    else
                    {
                        Console.WriteLine(line);
                    }
                }
                catch (Exception consoleException)
                {
                    Debug.WriteLine($"Logger console failure: {consoleException}");
                }
            }
        }
    }
