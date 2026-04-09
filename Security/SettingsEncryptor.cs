using System;
using System.Security.Cryptography;
using System.Text;
using TaskManager.Utils;

namespace TaskManager.Security
{
    /// <summary>
    /// Encrypts and decrypts sensitive settings values using Windows DPAPI.
    ///
    /// Why DPAPI:
    ///   - No key management — encryption is tied to the Windows user account
    ///   - Data encrypted by user A cannot be decrypted by user B
    ///   - No hardcoded keys or secrets anywhere in code
    ///   - Built into Windows — no NuGet dependency needed
    ///
    /// Scope: DataProtectionScope.CurrentUser
    ///   Encrypted data is only decryptable by the same Windows user account.
    ///   If the user profile is deleted or moved, data is permanently lost.
    ///   This is acceptable for settings — they can be reset to defaults.
    ///
    /// Usage:
    ///   var cipher = SettingsEncryptor.Encrypt("my-api-token");
    ///   var plain  = SettingsEncryptor.Decrypt(cipher);
    /// </summary>
    public static class SettingsEncryptor
    {
        // Optional entropy adds extra protection — not required but recommended.
        // This ties the encryption to this specific application.
        private static readonly byte[] _entropy = Encoding.UTF8.GetBytes(
            "TaskManager.v1.Anthropic.2026");

        // ─── Public: Encrypt / Decrypt ────────────────────────────────────────

        /// <summary>
        /// Encrypts a plaintext string using DPAPI (CurrentUser scope).
        /// Returns a Base64-encoded ciphertext safe for JSON storage.
        /// Returns null if encryption fails (DPAPI unavailable).
        /// </summary>
        public static string? Encrypt(string plainText)
        {
            if (string.IsNullOrEmpty(plainText)) return string.Empty;

            try
            {
                var plainBytes = Encoding.UTF8.GetBytes(plainText);
                var cipherBytes = ProtectedData.Protect(
                    plainBytes, _entropy, DataProtectionScope.CurrentUser);

                return Convert.ToBase64String(cipherBytes);
            }
            catch (Exception ex)
            {
                Logger.Error($"SettingsEncryptor.Encrypt failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Decrypts a Base64-encoded ciphertext string produced by Encrypt().
        /// Returns null if decryption fails (wrong user, corrupted data, or
        /// DPAPI unavailable).
        /// </summary>
        public static string? Decrypt(string cipherText)
        {
            if (string.IsNullOrEmpty(cipherText)) return string.Empty;

            try
            {
                var cipherBytes = Convert.FromBase64String(cipherText);
                var plainBytes = ProtectedData.Unprotect(
                    cipherBytes, _entropy, DataProtectionScope.CurrentUser);

                return Encoding.UTF8.GetString(plainBytes);
            }
            catch (CryptographicException)
            {
                // Wrong user account, corrupted data, or entropy mismatch
                Logger.Warn("SettingsEncryptor.Decrypt failed — returning null. " +
                            "Setting will be reset to default.");
                return null;
            }
            catch (Exception ex)
            {
                Logger.Error($"SettingsEncryptor.Decrypt unexpected error: {ex.Message}");
                return null;
            }
        }

        // ─── Public: Safe Decrypt with Fallback ───────────────────────────────

        /// <summary>
        /// Decrypts a ciphertext, returning a fallback value if decryption fails.
        /// Use this when a failed decrypt should silently use a default.
        /// </summary>
        public static string DecryptOrDefault(string? cipherText, string defaultValue = "")
        {
            if (string.IsNullOrEmpty(cipherText)) return defaultValue;
            return Decrypt(cipherText) ?? defaultValue;
        }

        // ─── Public: Field-Level Helpers ──────────────────────────────────────

        /// <summary>
        /// Returns true if the given string appears to be a valid DPAPI ciphertext.
        /// Used to detect whether a settings field is already encrypted.
        /// </summary>
        public static bool LooksEncrypted(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            try
            {
                var bytes = Convert.FromBase64String(value);
                // DPAPI blobs start with a DPAPI header (at least 20 bytes)
                return bytes.Length >= 20;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Encrypts a value only if it is not already encrypted.
        /// Safe to call repeatedly on the same field.
        /// </summary>
        public static string? EncryptIfNeeded(string? value)
        {
            if (string.IsNullOrEmpty(value)) return value;
            if (LooksEncrypted(value)) return value; // already encrypted
            return Encrypt(value);
        }

        // ─── Public: Secure Wipe ──────────────────────────────────────────────

        /// <summary>
        /// Overwrites a byte array with zeros before releasing it.
        /// Use on any byte[] that held sensitive plaintext data.
        /// Managed GC cannot guarantee memory is zeroed — this is best-effort.
        /// </summary>
        public static void SecureWipe(byte[]? data)
        {
            if (data == null) return;
            Array.Clear(data, 0, data.Length);
        }
    }
}