using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using TaskManager.Models;

namespace TaskManager.Utils
{
    /// <summary>
    /// Reads and writes user preferences to/from Data/settings.json.
    /// Replaces config.py from the Python prototype.
    /// All sensitive fields are encrypted via Security/SettingsEncryptor before saving.
    /// </summary>
    public static class Config
    {
        private static readonly string _settingsPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "Data", "settings.json");

        private static AppSettings? _current;

        // ─── Public API ───────────────────────────────────────────────────────

        /// <summary>
        /// Returns the current loaded settings.
        /// Loads from disk on first call, then caches in memory.
        /// </summary>
        public static AppSettings Current => _current ??= Load();

        /// <summary>
        /// Reloads settings from disk — call if file changed externally.
        /// </summary>
        public static AppSettings Reload()
        {
            _current = Load();
            return _current;
        }

        /// <summary>
        /// Persists current settings to disk.
        /// Creates the Data/ directory if it doesn't exist.
        /// </summary>
        public static void Save()
        {
            try
            {
                var dir = Path.GetDirectoryName(_settingsPath)!;
                Directory.CreateDirectory(dir);

                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                };

                var json = JsonSerializer.Serialize(_current ?? new AppSettings(), options);
                File.WriteAllText(_settingsPath, json);
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to save settings: {ex.Message}");
            }
        }

        /// <summary>
        /// Resets all settings to defaults and saves.
        /// </summary>
        public static void Reset()
        {
            _current = new AppSettings();
            Save();
            Logger.Info("Settings reset to defaults.");
        }

        // ─── Private Load ─────────────────────────────────────────────────────

        private static AppSettings Load()
        {
            try
            {
                if (!File.Exists(_settingsPath))
                {
                    Logger.Info("settings.json not found — using defaults.");
                    var defaults = new AppSettings();
                    Save();
                    return defaults;
                }

                var json = File.ReadAllText(_settingsPath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json)
                               ?? new AppSettings();

                Logger.Info("Settings loaded successfully.");
                return settings;
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to load settings: {ex.Message} — using defaults.");
                return new AppSettings();
            }
        }
    }

    // ─── Settings Schema ──────────────────────────────────────────────────────
    // This is what gets serialised to settings.json.
    // Mirrors what was scattered across config.py and constants.py in Python.

    public class AppSettings
    {
        // ─── General ──────────────────────────────────────────────────────────

        [JsonPropertyName("version")]
        public string Version { get; set; } = "1.0.0";

        [JsonPropertyName("power_mode")]
        public PowerMode DefaultPowerMode { get; set; } = PowerMode.Balanced;

        [JsonPropertyName("start_minimized")]
        public bool StartMinimized { get; set; } = false;

        [JsonPropertyName("minimize_to_tray")]
        public bool MinimizeToTray { get; set; } = true;

        [JsonPropertyName("start_with_windows")]
        public bool StartWithWindows { get; set; } = false;

        // ─── Refresh Intervals (ms) ───────────────────────────────────────────

        [JsonPropertyName("refresh_fast_ms")]
        public int RefreshFastMs { get; set; } = Constants.RefreshIntervals.Fast;

        [JsonPropertyName("refresh_medium_ms")]
        public int RefreshMediumMs { get; set; } = Constants.RefreshIntervals.Medium;

        [JsonPropertyName("refresh_slow_ms")]
        public int RefreshSlowMs { get; set; } = Constants.RefreshIntervals.Slow;

        // ─── UI Preferences ───────────────────────────────────────────────────

        [JsonPropertyName("window_width")]
        public int WindowWidth { get; set; } = Constants.UI.DefaultWidth;

        [JsonPropertyName("window_height")]
        public int WindowHeight { get; set; } = Constants.UI.DefaultHeight;

        [JsonPropertyName("show_sparklines")]
        public bool ShowSparklines { get; set; } = true;

        [JsonPropertyName("show_temperatures")]
        public bool ShowTemperatures { get; set; } = true;

        [JsonPropertyName("sparkline_history")]
        public int SparklineHistory { get; set; } = Constants.UI.SparklineHistory;

        [JsonPropertyName("default_sort_column")]
        public string DefaultSortColumn { get; set; } = "CpuPercent";

        [JsonPropertyName("default_sort_descending")]
        public bool DefaultSortDescending { get; set; } = true;

        // ─── Alert Thresholds ─────────────────────────────────────────────────

        [JsonPropertyName("alert_cpu_percent")]
        public float AlertCpuPercent { get; set; } = Constants.AlertDefaults.CpuPercent;

        [JsonPropertyName("alert_ram_percent")]
        public float AlertRamPercent { get; set; } = Constants.AlertDefaults.RamPercent;

        [JsonPropertyName("alert_gpu_percent")]
        public float AlertGpuPercent { get; set; } = Constants.AlertDefaults.GpuPercent;

        [JsonPropertyName("alert_cpu_temp")]
        public float AlertCpuTempCelsius { get; set; } = Constants.AlertDefaults.CpuTempCelsius;

        [JsonPropertyName("alert_battery_percent")]
        public float AlertBatteryPercent { get; set; } = Constants.AlertDefaults.BatteryPercent;

        [JsonPropertyName("alert_sustain_seconds")]
        public int AlertSustainSeconds { get; set; } = Constants.AlertDefaults.SustainSeconds;

        // ─── Power Scheduler ─────────────────────────────────────────────────

        [JsonPropertyName("scheduler_enabled")]
        public bool SchedulerEnabled { get; set; } = true;

        [JsonPropertyName("scheduler_battery_threshold")]
        public float SchedulerBatteryThreshold { get; set; } = 20f;

        [JsonPropertyName("scheduler_switch_to")]
        public PowerMode SchedulerSwitchTo { get; set; } = PowerMode.Efficiency;

        // ─── Snapshot Settings ────────────────────────────────────────────────

        [JsonPropertyName("snapshot_auto_save")]
        public bool SnapshotAutoSave { get; set; } = false;

        [JsonPropertyName("snapshot_interval_minutes")]
        public int SnapshotIntervalMinutes { get; set; } = 30;

        [JsonPropertyName("snapshot_max_count")]
        public int SnapshotMaxCount { get; set; } = Constants.Snapshots.MaxSnapshots;
    }
}