using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace TaskManager.Utils
{
    /// <summary>
    /// Reusable pool of PerformanceCounter instances.
    ///
    /// Why this exists:
    ///   Creating a new PerformanceCounter is expensive (~50–200ms per counter).
    ///   If you create one every tick you'll burn CPU just on monitoring overhead.
    ///   This pool creates each counter once, caches it, and reuses it every read.
    ///
    /// Usage:
    ///   float cpu = PerfCounterPool.Read("Processor", "% Processor Time", "_Total");
    /// </summary>
    public static class PerfCounterPool
    {
        // Key = "category/counter/instance"
        private static readonly Dictionary<string, PerformanceCounter> _pool = new();
        private static readonly object _lock = new();

        // ─── Public API ───────────────────────────────────────────────────────

        /// <summary>
        /// Reads the next value from a cached PerformanceCounter.
        /// Creates and caches the counter on first call.
        /// Returns -1 if the counter is unavailable on this system.
        /// </summary>
        public static float Read(string category, string counter, string instance = "")
        {
            try
            {
                var pc = GetOrCreate(category, counter, instance);
                return pc.NextValue();
            }
            catch (Exception ex)
            {
                Logger.Warn($"PerfCounter read failed [{category}/{counter}/{instance}]: {ex.Message}");
                return -1f;
            }
        }

        /// <summary>
        /// Some counters (CPU %) need two reads with a delay to return a valid value.
        /// Call this version for those counters — it primes the counter on first call
        /// so subsequent single reads are accurate.
        /// </summary>
        public static float ReadWithPrime(string category, string counter, string instance = "")
        {
            var key = MakeKey(category, counter, instance);

            lock (_lock)
            {
                // If counter exists in pool it's already been primed
                if (_pool.ContainsKey(key))
                    return Read(category, counter, instance);

                // First time — create and prime (discard first value)
                try
                {
                    var pc = new PerformanceCounter(category, counter, instance, readOnly: true);
                    pc.NextValue(); // prime — first value is always 0
                    _pool[key] = pc;
                    return 0f;     // return 0 on first call — next call will be accurate
                }
                catch (Exception ex)
                {
                    Logger.Warn($"PerfCounter prime failed [{category}/{counter}/{instance}]: {ex.Message}");
                    return -1f;
                }
            }
        }

        /// <summary>
        /// Pre-warms a set of counters at startup so the first real read is accurate.
        /// Call this once during app init for all counters you know you'll need.
        /// </summary>
        public static void Prewarm(params (string category, string counter, string instance)[] counters)
        {
            foreach (var (cat, ctr, inst) in counters)
                ReadWithPrime(cat, ctr, inst);
        }

        /// <summary>
        /// Removes a specific counter from the pool and disposes it.
        /// Use when a per-process counter is no longer needed (process exited).
        /// </summary>
        public static void Release(string category, string counter, string instance = "")
        {
            var key = MakeKey(category, counter, instance);
            lock (_lock)
            {
                if (_pool.TryGetValue(key, out var pc))
                {
                    pc.Dispose();
                    _pool.Remove(key);
                }
            }
        }

        /// <summary>
        /// Disposes all counters and clears the pool.
        /// Call on application shutdown.
        /// </summary>
        public static void DisposeAll()
        {
            lock (_lock)
            {
                foreach (var pc in _pool.Values)
                {
                    try { pc.Dispose(); }
                    catch { /* best effort */ }
                }
                _pool.Clear();
            }
        }

        /// <summary>How many counters are currently cached</summary>
        public static int PoolSize
        {
            get { lock (_lock) return _pool.Count; }
        }

        // ─── Well-Known Counter Shortcuts ────────────────────────────────────
        // Avoid magic strings scattered across the codebase.

        /// <summary>Total CPU usage % across all cores (0–100)</summary>
        public static float CpuTotal()
            => ReadWithPrime("Processor", "% Processor Time", "_Total");

        /// <summary>Per-core CPU usage % — core index 0-based</summary>
        public static float CpuCore(int index)
            => ReadWithPrime("Processor", "% Processor Time", index.ToString());

        /// <summary>Available memory in bytes</summary>
        public static float RamAvailableBytes()
            => Read("Memory", "Available Bytes");

        /// <summary>Committed memory in bytes</summary>
        public static float RamCommittedBytes()
            => Read("Memory", "Committed Bytes");

        /// <summary>Total disk read bytes/sec across all drives</summary>
        public static float DiskReadBytesPerSec()
            => ReadWithPrime("PhysicalDisk", "Disk Read Bytes/sec", "_Total");

        /// <summary>Total disk write bytes/sec across all drives</summary>
        public static float DiskWriteBytesPerSec()
            => ReadWithPrime("PhysicalDisk", "Disk Write Bytes/sec", "_Total");

        /// <summary>Disk active time % — good indicator of disk pressure</summary>
        public static float DiskActivePercent()
            => ReadWithPrime("PhysicalDisk", "% Disk Time", "_Total");

        /// <summary>Network bytes received/sec — adapter name required</summary>
        public static float NetworkReceivedBytesPerSec(string adapterName)
            => ReadWithPrime("Network Interface", "Bytes Received/sec", adapterName);

        /// <summary>Network bytes sent/sec — adapter name required</summary>
        public static float NetworkSentBytesPerSec(string adapterName)
            => ReadWithPrime("Network Interface", "Bytes Sent/sec", adapterName);

        // ─── Private Helpers ──────────────────────────────────────────────────

        private static PerformanceCounter GetOrCreate(string category, string counter, string instance)
        {
            var key = MakeKey(category, counter, instance);
            lock (_lock)
            {
                if (!_pool.TryGetValue(key, out var pc))
                {
                    pc = new PerformanceCounter(category, counter, instance, readOnly: true);
                    _pool[key] = pc;
                }
                return pc;
            }
        }

        private static string MakeKey(string category, string counter, string instance)
            => $"{category}\x00{counter}\x00{instance}";
        // Using null char as separator — can't appear in counter names
    }
}