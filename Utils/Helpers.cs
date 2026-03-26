using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using TaskManager.Models;

namespace TaskManager.Utils
{
    /// <summary>
    /// Shared utility functions used across multiple modules.
    /// Replaces helpers.py from the Python prototype.
    /// Pure static functions — no state.
    /// </summary>
    public static class Helpers
    {
        // ─── Formatting ───────────────────────────────────────────────────────

        /// <summary>
        /// Formats bytes into a human-readable string.
        /// e.g. 1536 → "1.5 KB", 2097152 → "2.0 MB"
        /// </summary>
        public static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
            return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
        }

        /// <summary>
        /// Formats MB/s speed for display.
        /// e.g. 0.04 → "40 KB/s", 1.5 → "1.5 MB/s"
        /// </summary>
        public static string FormatSpeed(float mbps)
        {
            if (mbps < 0.1f) return $"{mbps * 1024:F0} KB/s";
            if (mbps < 100f) return $"{mbps:F1} MB/s";
            return $"{mbps / 1024:F2} GB/s";
        }

        /// <summary>
        /// Formats a percentage for display, clamped 0–100.
        /// e.g. 85.4321 → "85.4%"
        /// </summary>
        public static string FormatPercent(float value, int decimals = 1)
            => $"{Math.Clamp(value, 0, 100).ToString($"F{decimals}")}%";

        /// <summary>
        /// Formats a TimeSpan into a compact uptime string.
        /// e.g. 1d 4h 32m, 45m 12s
        /// </summary>
        public static string FormatUptime(TimeSpan uptime)
        {
            if (uptime.TotalDays >= 1)
                return $"{(int)uptime.TotalDays}d {uptime.Hours}h {uptime.Minutes}m";
            if (uptime.TotalHours >= 1)
                return $"{(int)uptime.TotalHours}h {uptime.Minutes}m";
            if (uptime.TotalMinutes >= 1)
                return $"{(int)uptime.TotalMinutes}m {uptime.Seconds}s";
            return $"{uptime.Seconds}s";
        }

        /// <summary>
        /// Formats a temperature with degree symbol and colour hint.
        /// e.g. 72.0 → "72°C"
        /// </summary>
        public static string FormatTemp(float celsius)
            => celsius < 0 ? "N/A" : $"{celsius:F0}°C";

        // ─── Power Impact ─────────────────────────────────────────────────────

        /// <summary>
        /// Calculates a power impact score from resource usage.
        /// Formula kept here so it's easy to tune in one place.
        /// </summary>
        public static float CalcImpactScore(float cpuPercent, float diskMBps, float networkMBps)
            => (cpuPercent * 1.5f) + (diskMBps * 0.5f) + (networkMBps * 2.0f);

        /// <summary>
        /// Maps an impact score to a PowerImpact enum level.
        /// Cutoffs come from Constants so they're easy to tune.
        /// </summary>
        public static PowerImpact ScoreToImpact(float score) => score switch
        {
            < 5f => PowerImpact.Low,
            < 20f => PowerImpact.Medium,
            < 45f => PowerImpact.High,
            _ => PowerImpact.VeryHigh,
        };

        /// <summary>
        /// Convenience — compute impact directly from a ProcessInfo.
        /// </summary>
        public static PowerImpact ComputeImpact(ProcessInfo p)
            => ScoreToImpact(CalcImpactScore(p.CpuPercent, p.DiskMBps, p.NetworkMBps));

        // ─── Process Helpers ──────────────────────────────────────────────────

        /// <summary>
        /// Classifies a process as App / Background / System.
        /// Order matters — System check runs first.
        /// </summary>
        public static ProcessType ClassifyProcess(Process p)
        {
            var name = p.ProcessName.ToLowerInvariant();

            if (Constants.SystemProcessNames.Contains(name))
                return ProcessType.System;

            if (Constants.BackgroundProcessNames.Contains(name))
                return ProcessType.Background;

            // Heuristic: session 0 processes are system/service
            if (p.SessionId == 0)
                return ProcessType.System;

            return ProcessType.App;
        }

        /// <summary>
        /// Safely gets the executable path for a process.
        /// Returns null if access is denied (common for system processes).
        /// </summary>
        public static string? TryGetExecutablePath(Process p)
        {
            try { return p.MainModule?.FileName; }
            catch { return null; }
        }

        /// <summary>
        /// Safely gets the process start time.
        /// Returns DateTime.MinValue if access is denied.
        /// </summary>
        public static DateTime TryGetStartTime(Process p)
        {
            try { return p.StartTime; }
            catch { return DateTime.MinValue; }
        }

        /// <summary>
        /// Safely gets the thread count.
        /// Returns 0 if access is denied.
        /// </summary>
        public static int TryGetThreadCount(Process p)
        {
            try { return p.Threads.Count; }
            catch { return 0; }
        }

        // ─── Network Helpers ──────────────────────────────────────────────────

        /// <summary>
        /// Returns the name of the first active non-loopback network adapter.
        /// Used to pick the right adapter for PerformanceCounter network reads.
        /// </summary>
        public static string? GetActiveNetworkAdapterName()
        {
            try
            {
                return NetworkInterface
                    .GetAllNetworkInterfaces()
                    .FirstOrDefault(n =>
                        n.OperationalStatus == OperationalStatus.Up &&
                        n.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                        n.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                    ?.Description;
            }
            catch
            {
                return null;
            }
        }

        // ─── File Helpers ─────────────────────────────────────────────────────

        /// <summary>
        /// Returns the app's Data/ directory path, creating it if needed.
        /// </summary>
        public static string GetDataDirectory()
        {
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data");
            Directory.CreateDirectory(path);
            return path;
        }

        /// <summary>
        /// Returns the Logs/ directory path, creating it if needed.
        /// </summary>
        public static string GetLogDirectory()
        {
            var path = Path.Combine(GetDataDirectory(), Constants.Logging.LogFolderName);
            Directory.CreateDirectory(path);
            return path;
        }

        /// <summary>
        /// Returns the Snapshots/ directory path, creating it if needed.
        /// </summary>
        public static string GetSnapshotDirectory()
        {
            var path = Path.Combine(GetDataDirectory(), Constants.Snapshots.FolderName);
            Directory.CreateDirectory(path);
            return path;
        }

        // ─── Maths ────────────────────────────────────────────────────────────

        /// <summary>Clamp a float between min and max.</summary>
        public static float Clamp(float value, float min, float max)
            => Math.Max(min, Math.Min(max, value));

        /// <summary>
        /// Linearly interpolates between two values.
        /// t = 0 returns a, t = 1 returns b.
        /// Used for smooth sparkline transitions.
        /// </summary>
        public static float Lerp(float a, float b, float t)
            => a + (b - a) * Math.Clamp(t, 0f, 1f);
    }
}