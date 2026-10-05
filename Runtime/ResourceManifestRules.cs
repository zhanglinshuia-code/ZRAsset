#nullable disable
using System;
using System.IO;
using System.Linq;

namespace ZRAsset
{
    // Shared by Unity and the publisher; no Unity or storage dependency.
    internal static class ResourceManifestRules
    {
        internal const int ArchiveHeaderSize = 8, ChunkSize = 64 * 1024, PrefixSize = 104;
        internal const string Algorithm = "aes256-cbc-hmacsha256-v1";
        internal static bool IsValidVersion(string version)
        {
            return version != null && version.Length <= 128 && IsSafeSegment(version);
        }

        internal static string BundlePrefix(string name)
        {
            return "p" + name.Length + "_" + name + "_";
        }

        internal static bool IsValidKeyId(string value)
        {
            return value != null && value.Length <= 64 && IsSafeSegment(value) &&
                    value.All(c => (char.IsLetterOrDigit(c) && c < 128) || c == '_' || c == '-');
        }

        internal static bool IsSafeSegment(string value)
        {
            if (string.IsNullOrEmpty(value) || value == "." || value == ".." || value.EndsWith(".", StringComparison.Ordinal)) {
                return false;
            }

            foreach (var c in value) {
                if (!(c >= 'a' && c <= 'z') && !(c >= 'A' && c <= 'Z') && !(c >= '0' && c <= '9') && c != '-' && c != '_' && c != '.') {
                    return false;
                }
            }

            var stem = value.Split('.')[0].ToUpperInvariant();
            return stem == "CON" || stem == "PRN" || stem == "AUX" || stem == "NUL"
                ? false
                : !(stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) &&
                stem[3] >= '0' && stem[3] <= '9');
        }
        internal static bool IsSha256(string value)
        {
            return IsHex(value, 64);
        }

        internal static bool IsHex(string value, int length)
        {
            if (value == null || value.Length != length) {
                return false;
            }

            foreach (var c in value) {
                if (!Uri.IsHexDigit(c)) {
                    return false;
                }
            }

            return true;
        }
        public static bool IsValidName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length > 48 || name[0] < 'a' || name[0] > 'z' ||
                !IsSafeSegment(name)) {
                return false;
            }

            foreach (var c in name) {
                if (!(c >= 'a' && c <= 'z') && !(c >= '0' && c <= '9') && c != '_' && c != '-') {
                    return false;
                }
            }

            return true;
        }
        public static long GetEncryptedSize(long plainSize)
        {
            if (plainSize < 0) {
                throw new ArgumentOutOfRangeException(nameof(plainSize));
            }

            long full = plainSize / ChunkSize, tail = plainSize % ChunkSize;
            return checked(PrefixSize + (full * (ChunkSize + 64L)) + (tail == 0 ? 0 : 48 + (((tail / 16) + 1) * 16)));
        }
        internal static void ValidateEncryption(BundleInfo info)
        {
            if (!info.IsEncrypted) {
                if (!string.IsNullOrEmpty(info.EncryptionKeyId) || info.UnencryptedSize != 0 || !string.IsNullOrEmpty(info.UnencryptedSha256)) {
                    throw new InvalidDataException("Unencrypted file declares encryption metadata.");
                }

                return;
            }
            if (info.Encryption != Algorithm || !IsValidKeyId(info.EncryptionKeyId) || !IsSha256(info.UnencryptedSha256) ||
                !IsSha256(info.Sha256) || info.UnencryptedSize < 0) {
                throw new InvalidDataException("Invalid encryption metadata.");
            }

            try {
                if (info.Size != GetEncryptedSize(info.UnencryptedSize)) {
                    throw new InvalidDataException("Encrypted size does not match the declared layout.");
                }
            }
            catch (OverflowException) { throw new InvalidDataException("Encrypted size overflow."); }
        }
    }
}
