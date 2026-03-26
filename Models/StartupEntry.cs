using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TaskManager.Models
{
    /// <summary>
    /// Represents a single Windows startup application entry.
    /// Read from the registry by StartupManager.cs.
    /// Registry paths:
    ///   HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Run  (user-level)
    ///   HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run  (system-level, needs admin)
    /// </summary>
    public class StartupEntry : INotifyPropertyChanged
    {
        // ─── Identity ─────────────────────────────────────────────────────────

        /// <summary>Registry value name — used as unique key</summary>
        public string RegistryKey { get; init; } = string.Empty;

        private string _name = string.Empty;
        /// <summary>Display name shown in UI</summary>
        public string Name
        {
            get => _name;
            set => SetField(ref _name, value);
        }

        private string _publisher = string.Empty;
        /// <summary>Publisher/company name from executable metadata</summary>
        public string Publisher
        {
            get => _publisher;
            set => SetField(ref _publisher, value);
        }

        private string _executablePath = string.Empty;
        /// <summary>Full path to the executable</summary>
        public string ExecutablePath
        {
            get => _executablePath;
            set => SetField(ref _executablePath, value);
        }

        private string _arguments = string.Empty;
        /// <summary>Command line arguments passed at startup</summary>
        public string Arguments
        {
            get => _arguments;
            set => SetField(ref _arguments, value);
        }

        // ─── Scope ────────────────────────────────────────────────────────────

        private StartupScope _scope;
        /// <summary>
        /// User = HKCU (no admin needed to toggle)
        /// System = HKLM (requires admin to toggle)
        /// </summary>
        public StartupScope Scope
        {
            get => _scope;
            set => SetField(ref _scope, value);
        }

        // ─── State ────────────────────────────────────────────────────────────

        private bool _isEnabled;
        /// <summary>
        /// Whether this entry is currently enabled.
        /// Toggling this writes to the registry via StartupManager.
        /// </summary>
        public bool IsEnabled
        {
            get => _isEnabled;
            set => SetField(ref _isEnabled, value);
        }

        // ─── Performance Impact ───────────────────────────────────────────────

        private StartupImpact _impact;
        /// <summary>
        /// Estimated boot-time performance impact.
        /// Calculated from executable size, historical load time, CPU spike on start.
        /// </summary>
        public StartupImpact Impact
        {
            get => _impact;
            set
            {
                if (SetField(ref _impact, value))
                    OnPropertyChanged(nameof(ImpactLabel));
            }
        }

        /// <summary>Human-readable impact label for the UI</summary>
        public string ImpactLabel => Impact switch
        {
            StartupImpact.Low => "Low impact",
            StartupImpact.Medium => "Medium impact",
            StartupImpact.High => "High impact",
            _ => "Unknown"
        };

        private float _lastBootTimeSec;
        /// <summary>How long this entry took to initialize on last boot (seconds)</summary>
        public float LastBootTimeSec
        {
            get => _lastBootTimeSec;
            set => SetField(ref _lastBootTimeSec, value);
        }

        // ─── Metadata ─────────────────────────────────────────────────────────

        private string _version = string.Empty;
        /// <summary>File version from executable metadata</summary>
        public string Version
        {
            get => _version;
            set => SetField(ref _version, value);
        }

        private DateTime _lastModified;
        /// <summary>When the registry entry or executable was last modified</summary>
        public DateTime LastModified
        {
            get => _lastModified;
            set => SetField(ref _lastModified, value);
        }

        private bool _requiresAdmin;
        /// <summary>True if toggling this entry requires elevation</summary>
        public bool RequiresAdmin
        {
            get => _requiresAdmin;
            set => SetField(ref _requiresAdmin, value);
        }

        // ─── INotifyPropertyChanged ───────────────────────────────────────────

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(name);
            return true;
        }
    }

    // ─── Supporting Enums ─────────────────────────────────────────────────────

    public enum StartupScope
    {
        /// <summary>HKCU — current user only, no admin needed</summary>
        User,

        /// <summary>HKLM — all users, requires admin to modify</summary>
        System
    }

    public enum StartupImpact
    {
        Unknown,
        Low,
        Medium,
        High
    }
}