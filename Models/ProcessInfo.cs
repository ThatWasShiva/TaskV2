using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TaskManager.Models
{
    /// <summary>
    /// Represents a snapshot of a single running process.
    /// Implements INotifyPropertyChanged so WPF UI updates automatically
    /// when values change without rebuilding the entire list.
    /// </summary>
    public class ProcessInfo : INotifyPropertyChanged
    {
        // ─── Identity ────────────────────────────────────────────────────────

        private int _pid;
        public int Pid
        {
            get => _pid;
            set => SetField(ref _pid, value);
        }

        private string _name = string.Empty;
        /// <summary>Raw executable name e.g. "chrome.exe"</summary>
        public string Name
        {
            get => _name;
            set => SetField(ref _name, value);
        }

        private string _displayName = string.Empty;
        /// <summary>Friendly display name e.g. "Google Chrome"</summary>
        public string DisplayName
        {
            get => _displayName;
            set => SetField(ref _displayName, value);
        }

        private ProcessType _type;
        /// <summary>App / Background / System</summary>
        public ProcessType Type
        {
            get => _type;
            set => SetField(ref _type, value);
        }

        // ─── Resource Usage ───────────────────────────────────────────────────

        private float _cpuPercent;
        /// <summary>CPU usage as a percentage 0–100</summary>
        public float CpuPercent
        {
            get => _cpuPercent;
            set => SetField(ref _cpuPercent, value);
        }

        private long _ramBytes;
        /// <summary>RAM usage in bytes — use RamMB for display</summary>
        public long RamBytes
        {
            get => _ramBytes;
            set
            {
                if (SetField(ref _ramBytes, value))
                    OnPropertyChanged(nameof(RamMB)); // notify derived property
            }
        }

        /// <summary>Derived — RAM in MB for display</summary>
        public long RamMB => _ramBytes / (1024 * 1024);

        private float _diskMBps;
        /// <summary>Disk read+write speed in MB/s</summary>
        public float DiskMBps
        {
            get => _diskMBps;
            set => SetField(ref _diskMBps, value);
        }

        private float _networkMBps;
        /// <summary>Combined upload + download in MB/s</summary>
        public float NetworkMBps
        {
            get => _networkMBps;
            set => SetField(ref _networkMBps, value);
        }

        private float _networkUpMBps;
        public float NetworkUpMBps
        {
            get => _networkUpMBps;
            set => SetField(ref _networkUpMBps, value);
        }

        private float _networkDownMBps;
        public float NetworkDownMBps
        {
            get => _networkDownMBps;
            set => SetField(ref _networkDownMBps, value);
        }

        private float _gpuPercent;
        /// <summary>GPU usage percentage if available, -1 if not tracked</summary>
        public float GpuPercent
        {
            get => _gpuPercent;
            set => SetField(ref _gpuPercent, value);
        }

        // ─── Power Impact ─────────────────────────────────────────────────────

        private PowerImpact _powerImpact;
        /// <summary>Heuristic power impact level: Low / Medium / High / VeryHigh</summary>
        public PowerImpact PowerImpact
        {
            get => _powerImpact;
            set => SetField(ref _powerImpact, value);
        }

        private float _estimatedWatts;
        /// <summary>Estimated power draw in watts (from PowerEstimator)</summary>
        public float EstimatedWatts
        {
            get => _estimatedWatts;
            set => SetField(ref _estimatedWatts, value);
        }

        // ─── Status ───────────────────────────────────────────────────────────

        private ProcessStatus _status = ProcessStatus.Running;
        public ProcessStatus Status
        {
            get => _status;
            set => SetField(ref _status, value);
        }

        private int _priority;
        /// <summary>Windows priority class value</summary>
        public int Priority
        {
            get => _priority;
            set => SetField(ref _priority, value);
        }

        private string _priorityLabel = string.Empty;
        /// <summary>Human-readable priority e.g. "Normal", "High"</summary>
        public string PriorityLabel
        {
            get => _priorityLabel;
            set => SetField(ref _priorityLabel, value);
        }

        private bool _isProtected;
        /// <summary>True for critical system processes that should not be killed</summary>
        public bool IsProtected
        {
            get => _isProtected;
            set => SetField(ref _isProtected, value);
        }

        // ─── Metadata ─────────────────────────────────────────────────────────

        private DateTime _startTime;
        public DateTime StartTime
        {
            get => _startTime;
            set => SetField(ref _startTime, value);
        }

        /// <summary>Derived — how long the process has been running</summary>
        public TimeSpan Uptime => DateTime.Now - _startTime;

        private int _threadCount;
        public int ThreadCount
        {
            get => _threadCount;
            set => SetField(ref _threadCount, value);
        }

        private int _sessionId;
        /// <summary>Windows session ID — session 0 = system services</summary>
        public int SessionId
        {
            get => _sessionId;
            set => SetField(ref _sessionId, value);
        }

        private DateTime _lastUpdated;
        /// <summary>Timestamp of the last data refresh for this process</summary>
        public DateTime LastUpdated
        {
            get => _lastUpdated;
            set => SetField(ref _lastUpdated, value);
        }

        // ─── Lazy-loaded (only fetched on demand, not every tick) ─────────────

        /// <summary>Full executable path — loaded only when user requests details</summary>
        public string? ExecutablePath { get; set; }

        /// <summary>Command line arguments — loaded only on demand</summary>
        public string? CommandLine { get; set; }

        /// <summary>Process owner username — loaded only on demand</summary>
        public string? Owner { get; set; }

        // ─── Update Helper ────────────────────────────────────────────────────

        /// <summary>
        /// Patches only the fields that change every tick.
        /// Avoids replacing the whole object (which would cause full UI re-render).
        /// </summary>
        public void UpdateFrom(ProcessInfo newer)
        {
            CpuPercent = newer.CpuPercent;
            RamBytes = newer.RamBytes;
            DiskMBps = newer.DiskMBps;
            NetworkMBps = newer.NetworkMBps;
            NetworkUpMBps = newer.NetworkUpMBps;
            NetworkDownMBps = newer.NetworkDownMBps;
            GpuPercent = newer.GpuPercent;
            PowerImpact = newer.PowerImpact;
            EstimatedWatts = newer.EstimatedWatts;
            Status = newer.Status;
            ThreadCount = newer.ThreadCount;
            LastUpdated = newer.LastUpdated;
        }

        /// <summary>
        /// Returns true if any display-relevant field differs from another snapshot.
        /// Used to skip UI updates when nothing changed.
        /// </summary>
        public bool HasChanged(ProcessInfo other) =>
            Math.Abs(CpuPercent - other.CpuPercent) > 0.1f ||
            Math.Abs(RamBytes - other.RamBytes) > 512 * 1024 || // 0.5 MB threshold
            Math.Abs(DiskMBps - other.DiskMBps) > 0.05f ||
            Math.Abs(NetworkMBps - other.NetworkMBps) > 0.05f ||
            PowerImpact != other.PowerImpact ||
            Status != other.Status;

        // ─── INotifyPropertyChanged ───────────────────────────────────────────

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        /// <summary>
        /// Sets a backing field and fires PropertyChanged only if the value
        /// actually changed — avoids redundant UI redraws.
        /// </summary>
        protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(name);
            return true;
        }
    }

    // ─── Supporting Enums ─────────────────────────────────────────────────────

    public enum ProcessType
    {
        App,
        Background,
        System
    }

    public enum ProcessStatus
    {
        Running,
        Suspended,
        NotResponding
    }

    public enum PowerImpact
    {
        Low,
        Medium,
        High,
        VeryHigh
    }
}