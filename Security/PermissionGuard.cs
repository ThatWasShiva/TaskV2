using System;
using System.Diagnostics;
using System.Security.Principal;
using TaskManager.Utils;

namespace TaskManager.Security
{
    /// <summary>
    /// Centralises all UAC and privilege checks.
    /// Replaces scattered admin checks across the Python prototype.
    ///
    /// Design rules:
    ///   - Check privilege level before any sensitive operation
    ///   - Never silently elevate — always ask user first
    ///   - Cache IsAdmin result — WindowsIdentity query is not free
    ///   - All elevation attempts logged
    /// </summary>
    public static class PermissionGuard
    {
        // ─── Cached Admin State ───────────────────────────────────────────────
        // Re-evaluated only when explicitly invalidated (e.g. after UAC prompt).

        private static bool? _isAdminCache;
        private static bool? _isElevatedCache;

        // ─── Public: Queries ──────────────────────────────────────────────────

        /// <summary>
        /// Returns true if the current process is running with administrator privileges.
        /// Result is cached — call InvalidateCache() after elevation attempts.
        /// </summary>
        public static bool IsAdmin()
        {
            if (_isAdminCache.HasValue) return _isAdminCache.Value;

            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                _isAdminCache = principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                _isAdminCache = false;
            }

            return _isAdminCache.Value;
        }

        /// <summary>
        /// Returns true if the process token is elevated (UAC elevation active).
        /// On non-UAC systems this matches IsAdmin().
        /// </summary>
        public static bool IsElevated()
        {
            if (_isElevatedCache.HasValue) return _isElevatedCache.Value;

            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                // TOKEN_ELEVATION_TYPE: 1 = default, 2 = full, 3 = limited
                _isElevatedCache = identity.Owner?.IsWellKnown(
                    WellKnownSidType.BuiltinAdministratorsSid) ?? false;
            }
            catch
            {
                _isElevatedCache = false;
            }

            return _isElevatedCache.Value;
        }

        /// <summary>
        /// Returns true if the current user belongs to the Administrators group
        /// but the process is NOT currently elevated (UAC split token scenario).
        /// In this state, elevation is possible via a UAC prompt.
        /// </summary>
        public static bool CanElevate() => !IsAdmin() && IsInAdministratorsGroup();

        /// <summary>
        /// Returns the current privilege level as a human-readable label.
        /// Used in the status bar and settings UI.
        /// </summary>
        public static string PrivilegeLevelLabel() => IsAdmin()
            ? "Administrator"
            : CanElevate() ? "Standard (elevation available)" : "Standard User";

        // ─── Public: Require / Guard ──────────────────────────────────────────

        /// <summary>
        /// Returns a GuardResult indicating whether the caller can proceed.
        /// Use before any sensitive operation instead of throwing directly.
        ///
        /// Example:
        ///   var guard = PermissionGuard.Require(Privilege.Admin, "kill system process");
        ///   if (!guard.Allowed) { ShowError(guard.Reason); return; }
        /// </summary>
        public static GuardResult Require(Privilege required, string context = "")
        {
            switch (required)
            {
                case Privilege.Admin:
                    if (IsAdmin()) return GuardResult.Allow();
                    Logger.Warn($"Admin privilege required [{context}] — not elevated.");
                    return GuardResult.Deny(
                        "This action requires administrator privileges.\n" +
                        "Restart Task Manager as administrator to proceed.",
                        canElevate: CanElevate());

                case Privilege.Standard:
                    return GuardResult.Allow(); // always allowed

                case Privilege.SystemProcess:
                    if (IsAdmin()) return GuardResult.Allow();
                    Logger.Warn($"System process action blocked [{context}] — not admin.");
                    return GuardResult.Deny(
                        "Modifying system processes requires administrator privileges.");

                default:
                    return GuardResult.Deny("Unknown privilege requirement.");
            }
        }

        // ─── Public: Elevation ────────────────────────────────────────────────

        /// <summary>
        /// Relaunches the application with UAC elevation (runas verb).
        /// The current instance exits after requesting elevation.
        /// Returns false if the user cancelled the UAC prompt.
        /// </summary>
        public static bool RequestElevation()
        {
            try
            {
                Logger.Action("Requesting UAC elevation — relaunching.");

                var psi = new ProcessStartInfo
                {
                    FileName = Environment.ProcessPath
                                      ?? Process.GetCurrentProcess().MainModule!.FileName,
                    UseShellExecute = true,
                    Verb = "runas",   // triggers UAC prompt
                };

                Process.Start(psi);
                Logger.Action("Elevation request accepted — shutting down current instance.");

                // Shutdown current non-elevated instance
                System.Windows.Application.Current.Shutdown();
                return true;
            }
            catch (System.ComponentModel.Win32Exception ex)
                when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED
            {
                Logger.Action("UAC elevation cancelled by user.", success: false);
                return false;
            }
            catch (Exception ex)
            {
                Logger.Error($"Elevation request failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Clears the cached admin/elevation state.
        /// Call after any elevation attempt so IsAdmin() re-evaluates.
        /// </summary>
        public static void InvalidateCache()
        {
            _isAdminCache = null;
            _isElevatedCache = null;
        }

        // ─── Private ──────────────────────────────────────────────────────────

        private static bool IsInAdministratorsGroup()
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                foreach (var group in identity.Groups ?? [])
                {
                    if (group.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid))
                        return true;
                }
                return false;
            }
            catch { return false; }
        }
    }

    // ─── Supporting Types ─────────────────────────────────────────────────────

    public enum Privilege
    {
        Standard,       // any user
        Admin,          // requires administrator
        SystemProcess,  // requires administrator + extra confirmation
    }

    public class GuardResult
    {
        public bool Allowed { get; private init; }
        public string Reason { get; private init; } = string.Empty;
        public bool CanElevate { get; private init; }

        public static GuardResult Allow()
            => new() { Allowed = true };

        public static GuardResult Deny(string reason, bool canElevate = false)
            => new() { Allowed = false, Reason = reason, CanElevate = canElevate };
    }
}