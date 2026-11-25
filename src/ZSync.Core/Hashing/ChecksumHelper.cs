using System;
using System.Buffers.Binary;
using System.IO.Hashing;
using System.Security.Cryptography;

namespace ZSync.Core.Hashing
{
    /// <summary>
    /// Unified strong checksum provider supporting all HDiffPatch and ZSync checksum algorithms:
    /// xxh128 (XXH3 128-bit), md5, sha256, sha512, crc32, md4, sha1, and xxh64.
    /// </summary>
    public static class ChecksumHelper
    {
        public static int GetChecksumByteSize(string checksumType)
        {
            if (checksumType.Equals("xxh128", StringComparison.OrdinalIgnoreCase))
                return 16;
            if (checksumType.StartsWith("md5", StringComparison.OrdinalIgnoreCase))
                return 16;
            if (checksumType.StartsWith("sha512", StringComparison.OrdinalIgnoreCase))
                return 64;
            if (checksumType.StartsWith("sha256", StringComparison.OrdinalIgnoreCase))
                return 32;
            if (checksumType.StartsWith("crc32", StringComparison.OrdinalIgnoreCase))
                return 4;
            if (checksumType.StartsWith("md4", StringComparison.OrdinalIgnoreCase))
                return 16;
            if (checksumType.StartsWith("sha1", StringComparison.OrdinalIgnoreCase))
                return 20;

            // Default / xxh64
            return 8;
        }

        public static byte[] ComputeStrongChecksum(string checksumType, ReadOnlySpan<byte> data)
        {
            int size = GetChecksumByteSize(checksumType);
            byte[] result = new byte[size];
            ComputeStrongChecksum(checksumType, data, result);
            return result;
        }

        public static void ComputeStrongChecksum(string checksumType, ReadOnlySpan<byte> data, Span<byte> destination)
        {
            if (checksumType.Equals("xxh128", StringComparison.OrdinalIgnoreCase))
            {
                // System.IO.Hashing XxHash128 writes big-endian (high64 then low64).
                // HDiffPatch xxh128 plugin writes little-endian (low64 then high64), which is exact 16-byte reverse.
                Span<byte> target = destination.Slice(0, 16);
                XxHash128.Hash(data, target);
                target.Reverse();
                return;
            }
            if (checksumType.StartsWith("md5", StringComparison.OrdinalIgnoreCase))
            {
                MD5.HashData(data, destination.Slice(0, 16));
                return;
            }
            if (checksumType.StartsWith("sha512", StringComparison.OrdinalIgnoreCase))
            {
                SHA512.HashData(data, destination.Slice(0, 64));
                return;
            }
            if (checksumType.StartsWith("sha256", StringComparison.OrdinalIgnoreCase))
            {
                SHA256.HashData(data, destination.Slice(0, 32));
                return;
            }
            if (checksumType.StartsWith("crc32", StringComparison.OrdinalIgnoreCase))
            {
                Crc32.Hash(data, destination.Slice(0, 4));
                return;
            }
            if (checksumType.StartsWith("md4", StringComparison.OrdinalIgnoreCase))
            {
                Md4.Hash(data, destination.Slice(0, 16));
                return;
            }
            if (checksumType.StartsWith("sha1", StringComparison.OrdinalIgnoreCase))
            {
                SHA1.HashData(data, destination.Slice(0, 20));
                return;
            }

            // Default / xxh64:
            ulong h = FastXxHash64.Hash(data);
            BinaryPrimitives.WriteUInt64LittleEndian(destination.Slice(0, 8), h);
        }
    }
}
