using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using TaskManager.Models;
using TaskManager.Utils;

namespace TaskManager.Core
{
    /// <summary>
    /// Reads and manages Windows startup applications from the registry.
    /// Replaces startup_manager.py from the Python prototype.
    ///
    /// Registry paths used:
    ///   HKCU\...\Run          — user startup entries (no admin needed)
    ///   HKLM\...\Run          — system startup entries (admin needed to modify)
    ///   HKCU\...\StartupApproved\Run — enabled/disabled state (Windows 8+)
    ///
    /// Design rules:
    ///   - Read HKCU without elevation always
    ///   - Read HKLM entries but mark them RequiresAdmin = true
    ///   - Never write to HKLM without checking PermissionGuard first
    ///   - All registry writes logged via Logger.Action()
    /// </summary>
    public class StartupManager
    {
        // ─── Public: List ─────────────────────────────────────────────────────

        /// <summary>
        /// Returns all startup entries from both HKCU and HKLM.
        /// HKLM entries are marked RequiresAdmin = true.
        /// </summary>
        public IReadOnlyList<StartupEntry> GetAll()
        {
            var entries = new List<StartupEntry>();

            entries.AddRange(ReadFromHive(Registry.CurrentUser, StartupScope.User));
            entries.AddRange(ReadFromHive(Registry.LocalMachine, StartupScope.System));

            Logger.Info($"Startup entries loaded: {entries.Count}");
            return entries;
        }

        // ─── Public: Enable / Disable ─────────────────────────────────────────

        /// <summary>
        /// Enables or disables a startup entry.
        /// For HKLM entries, caller must have verified admin access first.
        /// Uses the StartupApproved key (Windows 8+) to preserve the original path.
        /// </summary>
        public bool SetEnabled(StartupEntry entry, bool enabled)
        {
            try
            {
                var hive = entry.Scope == StartupScope.User
                           ? Registry.CurrentUser
                           : Registry.LocalMachine;

                // Windows 8+ approach: write to StartupApproved key
                // Value: 02 00 00 00 00 00 00 00 00 00 00 00 = enabled
                //        03 00 00 00 00 00 00 00 00 00 00 00 = disabled
                using var approvedKey = hive.OpenSubKey(
                    Constants.RegistryPaths.StartupDisabledUser, writable: true);

                if (approvedKey != null)
                {
                    byte[] value = new byte[12];
                    value[0] = (byte)(enabled ? 0x02 : 0x03);
                    approvedKey.SetValue(entry.RegistryKey, value,
                                        RegistryValueKind.Binary);
                }
                else
                {
                    // Fallback: delete the Run key entry to disable
                    using var runKey = hive.OpenSubKey(
                        Constants.RegistryPaths.StartupUser, writable: true);

                    if (!enabled)
                        runKey?.DeleteValue(entry.RegistryKey, throwOnMissingValue: false);
                    else
                        runKey?.SetValue(entry.RegistryKey,
                                         entry.ExecutablePath + " " + entry.Arguments,
                                         RegistryValueKind.String);
                }

                entry.IsEnabled = enabled;
                Logger.Action(
                    $"Startup entry '{entry.Name}' {(enabled ? "enabled" : "disabled")} " +
                    $"[{entry.Scope}]");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Action(
                    $"Startup toggle failed for '{entry.Name}': {ex.Message}",
                    success: false);
                return false;
            }
        }

        // ─── Private: Registry Reading ────────────────────────────────────────

        private static IEnumerable<StartupEntry> ReadFromHive(
            RegistryKey hive, StartupScope scope)
        {
            var entries = new List<StartupEntry>();

            try
            {
                using var runKey = hive.OpenSubKey(
                    Constants.RegistryPaths.StartupUser, writable: false);

                if (runKey == null) return entries;

                // Load the enabled/disabled state map
                var approvedMap = LoadApprovedMap(hive);

                foreach (var valueName in runKey.GetValueNames())
                {
                    try
                    {
                        var rawValue = runKey.GetValue(valueName)?.ToString() ?? string.Empty;
                        var (exePath, args) = ParseCommandLine(rawValue);

                        var entry = new StartupEntry
                        {
                            RegistryKey = valueName,
                            Name = GetFriendlyName(exePath, valueName),
                            Publisher = GetPublisher(exePath),
                            ExecutablePath = exePath,
                            Arguments = args,
                            Scope = scope,
                            IsEnabled = approvedMap.GetValueOrDefault(valueName, true),
                            RequiresAdmin = scope == StartupScope.System,
                            LastModified = GetLastModified(exePath),
                            Version = GetVersion(exePath),
                            Impact = EstimateImpact(exePath),
                        };

                        entries.Add(entry);
                    }
                    catch
                    {
                        // Skip malformed entries
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"Registry read failed [{scope}]: {ex.Message}");
            }

            return entries;
        }

        /// <summary>
        /// Reads the StartupApproved key to determine which entries are disabled.
        /// Returns a map of registry value name → enabled state.
        /// </summary>
        private static Dictionary<string, bool> LoadApprovedMap(RegistryKey hive)
        {
            var map = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var key = hive.OpenSubKey(
                    Constants.RegistryPaths.StartupDisabledUser, writable: false);

                if (key == null) return map;

                foreach (var name in key.GetValueNames())
                {
                    if (key.GetValue(name) is byte[] bytes && bytes.Length > 0)
                        map[name] = bytes[0] == 0x02; // 0x02 = enabled, 0x03 = disabled
                }
            }
            catch { }
            return map;
        }

        // ─── Private: Metadata Helpers ────────────────────────────────────────

        private static (string exePath, string args) ParseCommandLine(string raw)
        {
            raw = raw.Trim();
            if (raw.StartsWith("\""))
            {
                int end = raw.IndexOf('"', 1);
                if (end < 0) return (raw, string.Empty);
                var path = raw[1..end];
                var rest = raw[(end + 1)..].Trim();
                return (path, rest);
            }

            var space = raw.IndexOf(' ');
            if (space < 0) return (raw, string.Empty);
            return (raw[..space], raw[(space + 1)..].Trim());
        }

        private static string GetFriendlyName(string exePath, string fallback)
        {
            try
            {
                if (File.Exists(exePath))
                {
                    var desc = FileVersionInfo.GetVersionInfo(exePath).FileDescription;
                    if (!string.IsNullOrWhiteSpace(desc)) return desc;
                }
            }
            catch { }
            return fallback;
        }

        private static string GetPublisher(string exePath)
        {
            try
            {
                if (File.Exists(exePath))
                    return FileVersionInfo.GetVersionInfo(exePath).CompanyName ?? string.Empty;
            }
            catch { }
            return string.Empty;
        }

        private static string GetVersion(string exePath)
        {
            try
            {
                if (File.Exists(exePath))
                    return FileVersionInfo.GetVersionInfo(exePath).FileVersion ?? string.Empty;
            }
            catch { }
            return string.Empty;
        }

        private static DateTime GetLastModified(string exePath)
        {
            try { return File.GetLastWriteTime(exePath); }
            catch { return DateTime.MinValue; }
        }

        private static StartupImpact EstimateImpact(string exePath)
        {
            try
            {
                if (!File.Exists(exePath)) return StartupImpact.Unknown;
                var sizeKB = new FileInfo(exePath).Length / 1024;

                return sizeKB switch
                {
                    < 500 => StartupImpact.Low,
                    < 5000 => StartupImpact.Medium,
                    _ => StartupImpact.High,
                };
            }
            catch { return StartupImpact.Unknown; }
        }
    }
}