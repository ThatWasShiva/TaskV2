using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TaskManager.Models
{
    /// <summary>
    /// Configuration for a single alert rule.
    /// e.g. "Alert me when CPU exceeds 85% for more than 10 seconds"
    /// Implements INotifyPropertyChanged so the Alerts UI updates live.
    /// </summary>
    public class AlertConfig : INotifyPropertyChanged
    {
        // ─── Identity ─────────────────────────────────────────────────────────

        /// <summary>Unique key e.g. "cpu", "ram", "gpu", "cpuTemp"</summary>
        public string Key { get; init; } = string.Empty;

        private string _label = string.Empty;
        /// <summary>Display label e.g. "CPU Usage"</summary>
        public string Label
        {
            get => _label;
            set => SetField(ref _label, value);
        }

        private string _icon = string.Empty;
        /// <summary>Emoji or icon identifier for the UI</summary>
        public string Icon
        {
            get => _icon;
            set => SetField(ref _icon, value);
        }

        // ─── Rule ─────────────────────────────────────────────────────────────

        private bool _isEnabled;
        public bool IsEnabled
        {
            get => _isEnabled;
            set => SetField(ref _isEnabled, value);
        }

        private float _threshold;
        /// <summary>
        /// Trigger value — percentage (0–100) for CPU/RAM/GPU,
        /// or degrees Celsius for temperature alerts.
        /// </summary>
        public float Threshold
        {
            get => _threshold;
            set => SetField(ref _threshold, Math.Clamp(value, 0, 150));
        }

        private AlertMetric _metric;
        /// <summary>Which metric this rule watches</summary>
        public AlertMetric Metric
        {
            get => _metric;
            set => SetField(ref _metric, value);
        }

        private AlertSeverity _severity = AlertSeverity.Warning;
        public AlertSeverity Severity
        {
            get => _severity;
            set => SetField(ref _severity, value);
        }

        private int _sustainSeconds = 5;
        /// <summary>
        /// How many consecutive seconds the threshold must be exceeded
        /// before the alert fires — prevents false positives on spikes.
        /// </summary>
        public int SustainSeconds
        {
            get => _sustainSeconds;
            set => SetField(ref _sustainSeconds, Math.Max(1, value));
        }

        // ─── Power Scheduler ─────────────────────────────────────────────────

        private bool _autoSwitchMode;
        /// <summary>
        /// If true, automatically switch power mode when this alert fires.
        /// e.g. switch to Efficiency when battery drops below threshold.
        /// </summary>
        public bool AutoSwitchMode
        {
            get => _autoSwitchMode;
            set => SetField(ref _autoSwitchMode, value);
        }

        private PowerMode _switchToMode = PowerMode.Efficiency;
        /// <summary>Which power mode to switch to when alert fires</summary>
        public PowerMode SwitchToMode
        {
            get => _switchToMode;
            set => SetField(ref _switchToMode, value);
        }

        // ─── State ────────────────────────────────────────────────────────────

        private bool _isCurrentlyFired;
        /// <summary>True while the alert condition is active right now</summary>
        public bool IsCurrentlyFired
        {
            get => _isCurrentlyFired;
            set => SetField(ref _isCurrentlyFired, value);
        }

        private DateTime? _lastFiredAt;
        /// <summary>UTC timestamp of the last time this alert triggered</summary>
        public DateTime? LastFiredAt
        {
            get => _lastFiredAt;
            set => SetField(ref _lastFiredAt, value);
        }

        private int _consecutiveSeconds;
        /// <summary>
        /// Internal counter — how many consecutive seconds threshold has been exceeded.
        /// Alert fires when this reaches SustainSeconds.
        /// Managed by the UpdaterService, not persisted.
        /// </summary>
        public int ConsecutiveSeconds
        {
            get => _consecutiveSeconds;
            set => SetField(ref _consecutiveSeconds, value);
        }

        // ─── Derived ──────────────────────────────────────────────────────────

        /// <summary>Human-readable description for the UI</summary>
        public string Description =>
            IsEnabled
                ? $"Alert when {Label} exceeds {Threshold}{UnitSuffix} for {SustainSeconds}s"
                : "Alert disabled";

        private string UnitSuffix => Metric switch
        {
            AlertMetric.CpuTemp or
            AlertMetric.GpuTemp => "°C",
            _ => "%"
        };

        // ─── Factory Defaults ─────────────────────────────────────────────────

        /// <summary>
        /// Returns the default alert configs used on first launch.
        /// Mirrors what was in constants.py / config.py in the Python version.
        /// </summary>
        public static AlertConfig[] Defaults() => new[]
        {
            new AlertConfig
            {
                Key            = "cpu",
                Label          = "CPU Usage",
                Icon           = "💻",
                IsEnabled      = true,
                Threshold      = 85,
                Metric         = AlertMetric.Cpu,
                Severity       = AlertSeverity.Warning,
                SustainSeconds = 10,
            },
            new AlertConfig
            {
                Key            = "ram",
                Label          = "Memory Usage",
                Icon           = "🧮",
                IsEnabled      = true,
                Threshold      = 90,
                Metric         = AlertMetric.Ram,
                Severity       = AlertSeverity.Warning,
                SustainSeconds = 5,
            },
            new AlertConfig
            {
                Key            = "gpu",
                Label          = "GPU Usage",
                Icon           = "🎮",
                IsEnabled      = false,
                Threshold      = 95,
                Metric         = AlertMetric.Gpu,
                Severity       = AlertSeverity.Info,
                SustainSeconds = 10,
            },
            new AlertConfig
            {
                Key            = "cpuTemp",
                Label          = "CPU Temperature",
                Icon           = "🔥",
                IsEnabled      = true,
                Threshold      = 85,
                Metric         = AlertMetric.CpuTemp,
                Severity       = AlertSeverity.Critical,
                SustainSeconds = 5,
                AutoSwitchMode = true,
                SwitchToMode   = PowerMode.Efficiency,
            },
            new AlertConfig
            {
                Key            = "battery",
                Label          = "Battery Level",
                Icon           = "🔋",
                IsEnabled      = true,
                Threshold      = 20,
                Metric         = AlertMetric.Battery,
                Severity       = AlertSeverity.Warning,
                SustainSeconds = 1,
                AutoSwitchMode = true,
                SwitchToMode   = PowerMode.Efficiency,
            },
        };

        // ─── INotifyPropertyChanged ───────────────────────────────────────────

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(name);
            if (name != nameof(Description)) OnPropertyChanged(nameof(Description));
            return true;
        }
    }

    // ─── Supporting Enums ─────────────────────────────────────────────────────

    public enum AlertMetric
    {
        Cpu,
        Ram,
        Gpu,
        CpuTemp,
        GpuTemp,
        Disk,
        Network,
        Battery
    }

    public enum AlertSeverity
    {
        Info,
        Warning,
        Critical
    }
}