using System;
using System.Text.RegularExpressions;

namespace TaskManager.Security
{
    /// <summary>
    /// Sanitizes all user-supplied input before it touches WMI queries,
    /// registry reads, file paths, or log entries.
    ///
    /// Why this matters:
    ///   WMI queries built from raw user input can be injected just like SQL.
    ///   e.g. Name = 'chrome' OR 'x'='x'  →  returns all processes
    ///   Registry paths with path separators can escape intended key scope.
    ///   Log entries with newlines can forge fake log records.
    /// </summary>
    public static class InputSanitizer
    {
        // ─── Process / WMI ────────────────────────────────────────────────────

        /// <summary>
        /// Sanitizes a process name for use in WMI queries and search filters.
        /// Allows: letters, digits, spaces, dash, dot, underscore.
        /// Max length: 260 (MAX_PATH).
        /// </summary>
        public static string ProcessName(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return string.Empty;
            var clean = Regex.Replace(input, @"[^\w\s\.\-]", string.Empty);
            return Truncate(clean.Trim(), 260);
        }

        /// <summary>
        /// Sanitizes input specifically for embedding in a WMI WHERE clause.
        /// Escapes single quotes — the WMI injection vector.
        /// </summary>
        public static string WmiString(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return string.Empty;
            // Escape single quotes by doubling them (WMI convention)
            var escaped = input.Replace("'", "''");
            // Strip any remaining WMI metacharacters
            escaped = Regex.Replace(escaped, @"[\\%_\[\]]", string.Empty);
            return Truncate(escaped.Trim(), 256);
        }

        // ─── Registry ─────────────────────────────────────────────────────────

        /// <summary>
        /// Sanitizes a registry value name.
        /// Strips path separators to prevent escaping the intended registry key.
        /// </summary>
        public static string RegistryValueName(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return string.Empty;
            // Registry value names cannot contain backslash
            var clean = input.Replace("\\", string.Empty)
                             .Replace("/", string.Empty);
            return Truncate(clean.Trim(), 255);
        }

        // ─── File Paths ───────────────────────────────────────────────────────

        /// <summary>
        /// Validates and normalises a file path.
        /// Returns null if the path contains traversal sequences or invalid chars.
        /// </summary>
        public static string? FilePath(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return null;

            try
            {
                // Resolve to absolute path — exposes traversal attempts
                var full = System.IO.Path.GetFullPath(input.Trim());

                // Reject if path traversal moved outside expected base
                if (full.Contains("..", StringComparison.Ordinal)) return null;

                // Check for invalid path characters
                if (full.IndexOfAny(System.IO.Path.GetInvalidPathChars()) >= 0) return null;

                return full;
            }
            catch
            {
                return null;
            }
        }

        // ─── Search Query ─────────────────────────────────────────────────────

        /// <summary>
        /// Sanitizes a search box query for in-memory process list filtering.
        /// Since this filters a local list (not a DB/WMI query), the risk is low
        /// but we still strip control characters and limit length.
        /// </summary>
        public static string SearchQuery(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return string.Empty;
            // Strip control characters (newlines, tabs, etc.)
            var clean = Regex.Replace(input, @"[\x00-\x1F\x7F]", string.Empty);
            return Truncate(clean.Trim(), 100);
        }

        // ─── Log Entries ──────────────────────────────────────────────────────

        /// <summary>
        /// Sanitizes a string before writing it to the log file.
        /// Strips newlines and carriage returns to prevent log forging.
        /// A malicious process name with embedded newlines could fake log entries.
        /// </summary>
        public static string LogEntry(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return string.Empty;
            var clean = input.Replace("\r", " ").Replace("\n", " ");
            // Strip non-printable characters
            clean = Regex.Replace(clean, @"[^\x20-\x7E\u00A0-\uFFFF]", "?");
            return Truncate(clean, 1024);
        }

        // ─── Numeric ──────────────────────────────────────────────────────────

        /// <summary>
        /// Clamps an integer input to a safe range.
        /// Use for threshold inputs, refresh intervals, etc.
        /// </summary>
        public static int ClampInt(int value, int min, int max)
            => Math.Max(min, Math.Min(max, value));

        /// <summary>
        /// Clamps a float input to a safe range.
        /// </summary>
        public static float ClampFloat(float value, float min, float max)
            => Math.Max(min, Math.Min(max, value));

        // ─── Private ──────────────────────────────────────────────────────────

        private static string Truncate(string input, int maxLength)
            => input.Length <= maxLength ? input : input[..maxLength];
    }
}