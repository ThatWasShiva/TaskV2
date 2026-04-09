using System;
using System.Diagnostics;
using TaskManager.Models;
using TaskManager.Utils;

namespace TaskManager.Security
{
    /// <summary>
    /// Validates whether a process action (kill, suspend, priority) is permitted
    /// before ProcessManager executes it.
    ///
    /// Acts as a security gate between the UI and ProcessManager:
    ///   UI → ProcessAccessValidator → ProcessManager → Windows API
    ///
    /// Checks applied in order:
    ///   1. Is the process in the protected list?
    ///   2. Is the process a session-0 service? (needs admin)
    ///   3. Does the action need admin and are we elevated?
    ///   4. Is the process still alive?
    /// </summary>
    public static class ProcessAccessValidator
    {
        // ─── Public: Validate Actions ─────────────────────────────────────────

        /// <summary>
        /// Validates whether a Kill action is permitted on the given process.
        /// </summary>
        public static ValidationResult CanKill(ProcessInfo process)
        {
            // Protected system processes — hard block regardless of privilege
            if (process.IsProtected)
                return ValidationResult.Block(
                    $"{process.DisplayName} is a critical system process.\n" +
                    "Ending it will cause Windows to crash or restart.");

            // Session 0 processes are system services — need admin
            if (process.SessionId == 0 && !PermissionGuard.IsAdmin())
                return ValidationResult.RequireElevation(
                    $"Ending {process.DisplayName} requires administrator privileges.\n" +
                    "It is a system service running in session 0.");

            // Process not responding — allow kill with a warning
            if (process.Status == ProcessStatus.NotResponding)
                return ValidationResult.AllowWithWarning(
                    $"{process.DisplayName} is not responding.\n" +
                    "Ending it may cause unsaved data to be lost.");

            return ValidationResult.Allow();
        }

        /// <summary>
        /// Validates whether a Suspend action is permitted.
        /// Suspending critical processes can deadlock the system.
        /// </summary>
        public static ValidationResult CanSuspend(ProcessInfo process)
        {
            if (process.IsProtected)
                return ValidationResult.Block(
                    $"{process.DisplayName} cannot be suspended.\n" +
                    "Suspending critical system processes will freeze Windows.");

            if (process.SessionId == 0 && !PermissionGuard.IsAdmin())
                return ValidationResult.RequireElevation(
                    $"Suspending {process.DisplayName} requires administrator privileges.");

            if (process.Status == ProcessStatus.Suspended)
                return ValidationResult.Block(
                    $"{process.DisplayName} is already suspended.");

            return ValidationResult.Allow();
        }

        /// <summary>
        /// Validates whether a Resume action is permitted.
        /// </summary>
        public static ValidationResult CanResume(ProcessInfo process)
        {
            if (process.Status != ProcessStatus.Suspended)
                return ValidationResult.Block(
                    $"{process.DisplayName} is not suspended.");

            return ValidationResult.Allow();
        }

        /// <summary>
        /// Validates whether a priority change is permitted.
        /// Realtime priority is dangerous — restricted to admin only.
        /// </summary>
        public static ValidationResult CanSetPriority(
            ProcessInfo process, ProcessPriorityClass priority)
        {
            if (process.IsProtected)
                return ValidationResult.Block(
                    $"Cannot change priority of {process.DisplayName}.\n" +
                    "It is a protected system process.");

            if (priority == ProcessPriorityClass.RealTime && !PermissionGuard.IsAdmin())
                return ValidationResult.RequireElevation(
                    "Setting Realtime priority requires administrator privileges.\n" +
                    "Realtime processes can starve other processes and cause instability.");

            if (priority == ProcessPriorityClass.RealTime)
                return ValidationResult.AllowWithWarning(
                    "Realtime priority can cause system instability.\n" +
                    "Only use this for latency-critical applications.");

            return ValidationResult.Allow();
        }

        /// <summary>
        /// Validates whether we can read detailed info (path, cmdline) for a process.
        /// Some system processes deny all property access.
        /// </summary>
        public static ValidationResult CanReadDetails(ProcessInfo process)
        {
            if (process.SessionId == 0 && !PermissionGuard.IsAdmin())
                return ValidationResult.Block(
                    "Detailed information for system processes requires administrator.");

            return ValidationResult.Allow();
        }

        // ─── Public: Bulk Check ───────────────────────────────────────────────

        /// <summary>
        /// Quick check — returns true if any action can be performed on this process.
        /// Used to disable context menu items for fully protected processes.
        /// </summary>
        public static bool IsActionable(ProcessInfo process)
            => !process.IsProtected || PermissionGuard.IsAdmin();

        /// <summary>
        /// Returns the appropriate Privilege level required for an action.
        /// Used by PermissionGuard.Require() at the UI layer.
        /// </summary>
        public static Privilege RequiredPrivilege(ProcessInfo process) =>
            process.IsProtected || process.SessionId == 0
                ? Privilege.Admin
                : Privilege.Standard;
    }

    // ─── Result Type ──────────────────────────────────────────────────────────

    public enum ValidationOutcome
    {
        /// <summary>Action is permitted — proceed.</summary>
        Allow,

        /// <summary>Action is permitted but show the user a warning first.</summary>
        AllowWithWarning,

        /// <summary>Action requires admin — offer elevation prompt.</summary>
        RequireElevation,

        /// <summary>Action is blocked regardless of privilege.</summary>
        Block,
    }

    public class ValidationResult
    {
        public ValidationOutcome Outcome { get; private init; }
        public string Message { get; private init; } = string.Empty;

        public bool IsAllowed => Outcome is ValidationOutcome.Allow
                                                 or ValidationOutcome.AllowWithWarning;
        public bool NeedsElevation => Outcome == ValidationOutcome.RequireElevation;
        public bool IsBlocked => Outcome == ValidationOutcome.Block;
        public bool HasWarning => Outcome == ValidationOutcome.AllowWithWarning;

        public static ValidationResult Allow()
            => new() { Outcome = ValidationOutcome.Allow };

        public static ValidationResult AllowWithWarning(string message)
            => new() { Outcome = ValidationOutcome.AllowWithWarning, Message = message };

        public static ValidationResult RequireElevation(string message)
            => new() { Outcome = ValidationOutcome.RequireElevation, Message = message };

        public static ValidationResult Block(string message)
            => new() { Outcome = ValidationOutcome.Block, Message = message };
    }
}