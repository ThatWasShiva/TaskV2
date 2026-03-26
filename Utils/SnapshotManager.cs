using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using TaskManager.Models;

namespace TaskManager.Utils
{
    /// <summary>
    /// Saves and loads point-in-time snapshots of system metrics and process lists.
    /// Replaces the data/snapshots/ folder logic from the Python prototype.
    ///
    /// Snapshots are stored as JSON files in Data/Snapshots/.
    /// Oldest files are pruned automatically when the cap is reached.
    /// </summary>
    public static class SnapshotManager
    {
        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = true,
        };

        // ─── Save ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Saves a system metrics snapshot to disk.
        /// Filename format: snapshot_20260322_143045.json
        /// </summary>
        public static bool SaveMetrics(SystemMetrics metrics)
        {
            try
            {
                var dir = Helpers.GetSnapshotDirectory();
                var filename = $"snapshot_{DateTime.UtcNow:yyyyMMdd_HHmmss}{Constants.Snapshots.FileExtension}";
                var path = Path.Combine(dir, filename);

                var json = JsonSerializer.Serialize(metrics, _jsonOptions);
                File.WriteAllText(path, json);

                Logger.Action($"Snapshot saved: {filename}");
                PruneOldSnapshots(dir);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error($"Snapshot save failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Saves both metrics and a process list together as a combined snapshot.
        /// </summary>
        public static bool SaveFull(SystemMetrics metrics, IEnumerable<ProcessInfo> processes)
        {
            try
            {
                var dir = Helpers.GetSnapshotDirectory();
                var filename = $"full_{DateTime.UtcNow:yyyyMMdd_HHmmss}{Constants.Snapshots.FileExtension}";
                var path = Path.Combine(dir, filename);

                var snapshot = new FullSnapshot
                {
                    CapturedAt = DateTime.UtcNow,
                    Metrics = metrics,
                    Processes = processes.ToList(),
                };

                var json = JsonSerializer.Serialize(snapshot, _jsonOptions);
                File.WriteAllText(path, json);

                Logger.Action($"Full snapshot saved: {filename}");
                PruneOldSnapshots(dir);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error($"Full snapshot save failed: {ex.Message}");
                return false;
            }
        }

        // ─── Load ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Loads the most recent metrics snapshot from disk.
        /// Returns null if no snapshots exist.
        /// </summary>
        public static SystemMetrics? LoadLatestMetrics()
        {
            try
            {
                var dir = Helpers.GetSnapshotDirectory();
                var files = Directory.GetFiles(dir, $"snapshot_*{Constants.Snapshots.FileExtension}")
                                     .OrderByDescending(f => f)
                                     .ToArray();

                if (files.Length == 0) return null;

                var json = File.ReadAllText(files[0]);
                return JsonSerializer.Deserialize<SystemMetrics>(json);
            }
            catch (Exception ex)
            {
                Logger.Error($"Snapshot load failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Returns a list of all saved snapshot filenames with their timestamps.
        /// Used by the UI to show a snapshot history list.
        /// </summary>
        public static IReadOnlyList<SnapshotInfo> ListSnapshots()
        {
            try
            {
                var dir = Helpers.GetSnapshotDirectory();
                var files = Directory.GetFiles(dir, $"*{Constants.Snapshots.FileExtension}")
                                     .OrderByDescending(f => f);

                return files.Select(f => new SnapshotInfo
                {
                    FileName = Path.GetFileName(f),
                    FilePath = f,
                    CapturedAt = File.GetLastWriteTimeUtc(f),
                    FileSizeKB = (int)(new FileInfo(f).Length / 1024),
                }).ToList();
            }
            catch (Exception ex)
            {
                Logger.Error($"Snapshot list failed: {ex.Message}");
                return Array.Empty<SnapshotInfo>();
            }
        }

        /// <summary>
        /// Deletes all snapshots from disk.
        /// </summary>
        public static void ClearAll()
        {
            try
            {
                var dir = Helpers.GetSnapshotDirectory();
                var files = Directory.GetFiles(dir, $"*{Constants.Snapshots.FileExtension}");
                foreach (var f in files) File.Delete(f);
                Logger.Action($"Cleared {files.Length} snapshots.");
            }
            catch (Exception ex)
            {
                Logger.Error($"Snapshot clear failed: {ex.Message}");
            }
        }

        // ─── Private ──────────────────────────────────────────────────────────

        /// <summary>
        /// Removes oldest snapshots when count exceeds the configured maximum.
        /// </summary>
        private static void PruneOldSnapshots(string dir)
        {
            try
            {
                var files = Directory.GetFiles(dir, $"*{Constants.Snapshots.FileExtension}")
                                     .OrderBy(f => f)   // oldest first
                                     .ToArray();

                int toDelete = files.Length - Constants.Snapshots.MaxSnapshots;
                for (int i = 0; i < toDelete; i++)
                {
                    File.Delete(files[i]);
                    Logger.Debug($"Pruned snapshot: {Path.GetFileName(files[i])}");
                }
            }
            catch { /* pruning failure is non-fatal */ }
        }
    }

    // ─── Supporting Types ─────────────────────────────────────────────────────

    /// <summary>Combined snapshot of metrics + processes saved together</summary>
    public class FullSnapshot
    {
        public DateTime CapturedAt { get; set; }
        public SystemMetrics? Metrics { get; set; }
        public List<ProcessInfo> Processes { get; set; } = new();
    }

    /// <summary>Lightweight summary of a saved snapshot file — for listing in UI</summary>
    public class SnapshotInfo
    {
        public string FileName { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty;
        public DateTime CapturedAt { get; set; }
        public int FileSizeKB { get; set; }
    }
}