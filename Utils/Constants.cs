using System.Collections.Generic;
using TaskManager.Models;

namespace TaskManager.Utils
{
    /// <summary>
    /// Central place for all constant values, mappings, and thresholds.
    /// Replaces constants.py from the Python prototype.
    /// No logic here — pure data only.
    /// </summary>
    public static class Constants
    {
        // ─── Refresh Intervals (milliseconds) ────────────────────────────────
        // Tiered polling — not everything needs to update every second.

        public static class RefreshIntervals
        {
            /// <summary>CPU %, RAM % — user-visible metrics that feel "live"</summary>
            public const int Fast = 1000;

            /// <summary>Process list, disk I/O, network per process</summary>
            public const int Medium = 3000;

            /// <summary>GPU temp, battery, startup entries, WMI queries</summary>
            public const int Slow = 10000;
        }

        // ─── Temperature Thresholds (°C) ──────────────────────────────────────

        public static class TempThresholds
        {
            public const float CpuWarn = 70f;
            public const float CpuCritical = 85f;

            public const float GpuWarn = 75f;
            public const float GpuCritical = 90f;
        }

        // ─── Power Impact Score Cutoffs ───────────────────────────────────────
        // Score = (CpuPercent * 1.5) + (DiskMBps * 0.5) + (NetworkMBps * 2.0)

        public static class ImpactThresholds
        {
            public const float Low = 5f;
            public const float Medium = 20f;
            public const float High = 45f;
            // Anything above High = VeryHigh
        }

        // ─── Alert Defaults ───────────────────────────────────────────────────

        public static class AlertDefaults
        {
            public const float CpuPercent = 85f;
            public const float RamPercent = 90f;
            public const float GpuPercent = 95f;
            public const float CpuTempCelsius = 85f;
            public const float BatteryPercent = 20f;
            public const int SustainSeconds = 5;
        }

        // ─── Resource Usage Warning Levels ────────────────────────────────────

        public static class UsageThresholds
        {
            public const float Warn = 50f;
            public const float High = 75f;
            public const float Critical = 90f;
        }

        // ─── Windows Priority Classes ─────────────────────────────────────────
        // Maps Windows PROCESS_PRIORITY_CLASS values to human-readable labels.
        // Used by ProcessManager and Constants to display priority in the UI.

        public static readonly IReadOnlyDictionary<int, string> PriorityLabels
            = new Dictionary<int, string>
            {
                { 64,   "Idle" },
                { 16384,"Below Normal" },
                { 32,   "Normal" },
                { 32768,"Above Normal" },
                { 128,  "High" },
                { 256,  "Realtime" },
            };

        public static readonly IReadOnlyDictionary<string, int> PriorityValues
            = new Dictionary<string, int>
            {
                { "Idle",         64    },
                { "Below Normal", 16384 },
                { "Normal",       32    },
                { "Above Normal", 32768 },
                { "High",         128   },
                { "Realtime",     256   },
            };

        // ─── Windows Power Plan GUIDs ─────────────────────────────────────────
        // Used by ModeManager to switch power plans via WMI / powercfg.

        public static class PowerPlanGuids
        {
            public const string Performance = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
            public const string Balanced = "381b4222-f694-41f0-9685-ff5bb260df2e";
            public const string Efficiency = "a1841308-3541-4fab-bc81-f71556f20b4a";
        }

        // ─── Protected System Processes ───────────────────────────────────────
        // These processes must never be killed — doing so can crash Windows.
        // Used by ProcessAccessValidator.cs.

        public static readonly IReadOnlySet<string> ProtectedProcessNames
            = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase)
            {
                "lsass",       // Local Security Authority — kills = instant BSOD
                "csrss",       // Client/Server Runtime — kills = BSOD
                "smss",        // Session Manager
                "wininit",     // Windows Init
                "services",    // Service Control Manager
                "winlogon",    // Windows Logon
                "dwm",         // Desktop Window Manager
                "svchost",     // Service Host (generic)
                "system",      // System process
                "registry",    // Registry process
                "memory compression",
            };

        // ─── Process Type Classification ──────────────────────────────────────
        // Used by ProcessManager to classify processes into App/Background/System.

        public static readonly IReadOnlySet<string> SystemProcessNames
            = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase)
            {
                "lsass", "csrss", "smss", "wininit", "services",
                "winlogon", "dwm", "svchost", "audiodg", "taskhostw",
                "spoolsv", "searchindexer", "wuauclt", "msiexec",
                "conhost", "dllhost", "rundll32", "regsvr32",
            };

        public static readonly IReadOnlySet<string> BackgroundProcessNames
            = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase)
            {
                "onedrive", "dropbox", "steam", "discord", "slack",
                "searchindexer", "backgroundtaskhost", "runtimebroker",
                "sihost", "ctfmon", "igfxem", "igfxtray",
            };

        // ─── Registry Paths ───────────────────────────────────────────────────
        // Used by StartupManager — never hardcode these elsewhere.

        public static class RegistryPaths
        {
            public const string StartupUser =
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

            public const string StartupSystem =
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
            // Note: same key path, different hive (HKCU vs HKLM)

            public const string StartupDisabledUser =
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
        }

        // ─── Snapshot Settings ────────────────────────────────────────────────

        public static class Snapshots
        {
            public const int MaxSnapshots = 50;
            public const string FileExtension = ".json";
            public const string FolderName = "Snapshots";
        }

        // ─── Logging ──────────────────────────────────────────────────────────

        public static class Logging
        {
            public const int MaxLogFileSizeBytes = 5 * 1024 * 1024; // 5 MB
            public const int MaxLogFiles = 5;               // rotate after 5
            public const string LogFolderName = "Logs";
            public const string LogFilePrefix = "taskmanager_";
        }

        // ─── UI ───────────────────────────────────────────────────────────────

        public static class UI
        {
            /// <summary>Minimum ms between UI redraws — prevents flicker</summary>
            public const int MinRedrawIntervalMs = 250;

            /// <summary>Number of data points kept in sparkline history</summary>
            public const int SparklineHistory = 60;

            /// <summary>Default window dimensions</summary>
            public const int DefaultWidth = 1100;
            public const int DefaultHeight = 700;
        }
    }
}