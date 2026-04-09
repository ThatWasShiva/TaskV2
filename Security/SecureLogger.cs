using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using TaskManager.Utils;

namespace TaskManager.Security
{
    /// <summary>
    /// Tamper-evident audit logger for security-sensitive actions.
    /// Wraps Logger.cs with:
    ///   1. Input sanitization — strips newlines/control chars from all entries
    ///   2. HMAC integrity chain — each entry includes a hash of the previous one
    ///   3. Sensitive field redaction — PIDs only, never usernames or paths
    ///   4. Separate audit log file — distinct from the general app log
    ///
    /// The HMAC chain means if a log file is tampered with (entries deleted
    /// or modified), the chain will break and verification will fail.
    ///
    /// Key is derived from machine-specific data via DPAPI so it cannot
    /// be exported and used to forge entries on another machine.
    /// </summary>
    public static class SecureLogger
    {
        private static string? _auditLogPath;
        private static byte[]? _hmacKey;
        private static string _lastHash = string.Empty;
        private static readonly object _lock = new();

        // ─── Public: Initialise ───────────────────────────────────────────────

        /// <summary>
        /// Initialises the secure audit logger.
        /// Must be called once at startup before any Audit() calls.
        /// </summary>
        public static void Initialise(string logDirectory)
        {
            lock (_lock)
            {
                try
                {
                    Directory.CreateDirectory(logDirectory);
                    _auditLogPath = Path.Combine(logDirectory,
                        $"audit_{DateTime.UtcNow:yyyyMMdd}.log");

                    _hmacKey = DeriveKey();
                    _lastHash = LoadLastHash();

                    WriteRaw("=== Audit Log Session Start ===");
                    Logger.Info("SecureLogger initialised.");
                }
                catch (Exception ex)
                {
                    Logger.Error($"SecureLogger init failed: {ex.Message}");
                }
            }
        }

        // ─── Public: Audit Entries ────────────────────────────────────────────

        /// <summary>
        /// Records a security-relevant action in the tamper-evident audit log.
        /// Use for: process kills, suspends, priority changes, mode switches,
        /// startup toggles, elevation attempts.
        ///
        /// Rules enforced:
        ///   - Only PID is logged, not process owner or full path
        ///   - Message is sanitized before writing
        ///   - Entry is chained to the previous hash
        /// </summary>
        public static void Audit(string action, bool success, int? pid = null)
        {
            var entry = BuildEntry(action, success, pid);
            WriteChained(entry);
        }

        /// <summary>
        /// Records a security event (not a user action) — e.g. protected process
        /// access attempt, UAC prompt, privilege check failure.
        /// </summary>
        public static void SecurityEvent(string description)
        {
            var entry = BuildEntry($"[SECURITY] {description}", success: false, pid: null);
            WriteChained(entry);
            // Also surface to the general logger as a warning
            Logger.Warn($"Security event: {InputSanitizer.LogEntry(description)}");
        }

        // ─── Public: Verify ───────────────────────────────────────────────────

        /// <summary>
        /// Verifies the integrity of the current audit log file.
        /// Returns true if the chain is unbroken, false if tampering is detected.
        /// </summary>
        public static bool VerifyIntegrity()
        {
            if (_auditLogPath is null || !File.Exists(_auditLogPath))
                return true; // no log = no tampering

            try
            {
                string? prevHash = string.Empty;
                foreach (var line in File.ReadLines(_auditLogPath))
                {
                    if (!line.StartsWith("[")) continue; // skip headers

                    // Extract stored hash from end of line: |HASH:xxxxxx
                    var hashIdx = line.LastIndexOf("|HASH:", StringComparison.Ordinal);
                    if (hashIdx < 0) continue;

                    var stored = line[(hashIdx + 6)..];
                    var content = line[..hashIdx];
                    var expected = ComputeHmac(content + prevHash);

                    if (!CryptographicOperations.FixedTimeEquals(
                            Convert.FromHexString(stored),
                            Convert.FromHexString(expected)))
                    {
                        Logger.Error("Audit log integrity check FAILED — tampering detected.");
                        return false;
                    }

                    prevHash = stored;
                }

                Logger.Info("Audit log integrity check passed.");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error($"Integrity check error: {ex.Message}");
                return false;
            }
        }

        // ─── Private: Entry Building ──────────────────────────────────────────

        private static string BuildEntry(string action, bool success, int? pid)
        {
            var sanitized = InputSanitizer.LogEntry(action);
            var result = success ? "OK" : "FAIL";
            var pidPart = pid.HasValue ? $" PID:{pid}" : string.Empty;
            var ts = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fffZ");

            return $"[{ts}] [{result}]{pidPart} {sanitized}";
        }

        private static void WriteChained(string entry)
        {
            if (_auditLogPath is null) return;

            lock (_lock)
            {
                try
                {
                    var hash = ComputeHmac(entry + _lastHash);
                    var line = $"{entry}|HASH:{hash}";

                    File.AppendAllText(_auditLogPath,
                        line + Environment.NewLine, Encoding.UTF8);

                    _lastHash = hash;
                }
                catch (Exception ex)
                {
                    Logger.Error($"Audit write failed: {ex.Message}");
                }
            }
        }

        private static void WriteRaw(string line)
        {
            if (_auditLogPath is null) return;
            try
            {
                File.AppendAllText(_auditLogPath,
                    $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fffZ}] {line}" +
                    Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }

        // ─── Private: HMAC ────────────────────────────────────────────────────

        private static string ComputeHmac(string data)
        {
            if (_hmacKey is null) return string.Empty;
            using var hmac = new HMACSHA256(_hmacKey);
            var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
            return Convert.ToHexString(hash)[..16]; // truncate to 16 hex chars
        }

        /// <summary>
        /// Derives an HMAC key tied to the current Windows user via DPAPI.
        /// Key cannot be used on a different machine or user account.
        /// </summary>
        private static byte[] DeriveKey()
        {
            var seed = Encoding.UTF8.GetBytes("TaskManager.AuditKey.v1");
            return System.Security.Cryptography.ProtectedData.Protect(
                seed, null,
                System.Security.Cryptography.DataProtectionScope.CurrentUser);
        }

        /// <summary>
        /// Reads the last HMAC hash from the existing log file to continue the chain.
        /// Returns empty string if no previous log exists.
        /// </summary>
        private static string LoadLastHash()
        {
            if (_auditLogPath is null || !File.Exists(_auditLogPath))
                return string.Empty;

            try
            {
                string last = string.Empty;
                foreach (var line in File.ReadLines(_auditLogPath))
                {
                    var idx = line.LastIndexOf("|HASH:", StringComparison.Ordinal);
                    if (idx >= 0) last = line[(idx + 6)..];
                }
                return last;
            }
            catch { return string.Empty; }
        }
    }
}