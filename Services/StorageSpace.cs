using System;
using System.IO;
using System.Linq;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace YSLauncher.Services;

internal static class StorageSpace
{
    public const long GiB = 1024L * 1024 * 1024;

    public static long DownloadBudget(long archiveBytes, long minimumBytes) =>
        archiveBytes > 0 ? Math.Max(minimumBytes, checked(archiveBytes * 2 + 2 * GiB)) : minimumBytes;

    public static string ExistingDirectory(string destination)
    {
        string fullPath = Path.GetFullPath(destination);
        string root = Path.GetPathRoot(fullPath)!;
        string current = root;
        foreach (string part in fullPath[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            string next = Path.Combine(current, part);
            if (!Directory.Exists(next)) break;
            var info = new DirectoryInfo(next);
            current = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? next;
        }
        return current;
    }

    internal static string? FindVolume(string path, string[] volumes, StringComparison comparison) =>
        volumes.Where(volume =>
        {
            string normalized = Path.TrimEndingDirectorySeparator(volume);
            return path.Equals(normalized, comparison) ||
                path.StartsWith(Path.EndsInDirectorySeparator(normalized) ? normalized : normalized + Path.DirectorySeparatorChar, comparison);
        }).OrderByDescending(volume => volume.Length).FirstOrDefault();

    public static void EnsureAvailable(string destination, long requiredBytes)
    {
        string path = ExistingDirectory(destination);
        long available;
        if (OperatingSystem.IsWindows())
        {
            string directory = Path.EndsInDirectorySeparator(path) ? path : path + Path.DirectorySeparatorChar;
            if (!GetDiskFreeSpaceEx(directory, out ulong free, out _, out _))
                throw new IOException($"Cannot query disk space: {path}", new Win32Exception(Marshal.GetLastWin32Error()));
            available = (long)Math.Min(free, (ulong)long.MaxValue);
        }
        else
        {
            string? volume = FindVolume(path, DriveInfo.GetDrives().Select(drive => drive.Name).ToArray(), StringComparison.Ordinal);
            if (volume == null) throw new IOException($"Cannot determine the installation volume: {path}");
            available = new DriveInfo(volume).AvailableFreeSpace;
        }
        if (available < requiredBytes)
            throw new IOException(string.Format(I18n.T("LowDiskSpace"), GameUpdater.FormatBytes(requiredBytes)));
    }

    [DllImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceEx(string directory, out ulong available, out ulong total, out ulong free);
}
