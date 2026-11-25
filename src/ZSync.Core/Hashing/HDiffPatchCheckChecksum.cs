using System;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace ZSync.Core.Hashing
{
    /// <summary>
    /// Computes the order-independent XOR-accumulated check checksum used in HDiffPatch HSyni/HSynz formats.
    /// </summary>
    public sealed class HDiffPatchCheckChecksum
    {
        private readonly byte[] _dXor;
        private readonly string _checksumType;
        private readonly int _byteSize;

        public HDiffPatchCheckChecksum(string checksumType, int byteSize)
        {
            _checksumType = checksumType;
            _byteSize = byteSize;
            _dXor = new byte[byteSize];
            Array.Fill(_dXor, (byte)0xFF);
        }

        public void AppendBlock(int blockIndex, ReadOnlySpan<byte> blockChecksum)
        {
            Span<byte> indexBuf = stackalloc byte[12];
            BinaryPrimitives.WriteUInt32LittleEndian(indexBuf.Slice(0, 4), (uint)blockIndex);
            ulong fchecksum = FastAdler64.Compute(indexBuf.Slice(0, 4));
            BinaryPrimitives.WriteUInt32LittleEndian(indexBuf.Slice(4, 4), (uint)fchecksum);
            BinaryPrimitives.WriteUInt32LittleEndian(indexBuf.Slice(8, 4), (uint)(fchecksum >> 32));

            Span<byte> combined = stackalloc byte[12 + _byteSize];
            indexBuf.CopyTo(combined.Slice(0, 12));
            blockChecksum.Slice(0, _byteSize).CopyTo(combined.Slice(12, _byteSize));

            Span<byte> hashed = stackalloc byte[_byteSize];
            if (_checksumType.StartsWith("md5", StringComparison.OrdinalIgnoreCase))
            {
                MD5.HashData(combined, hashed);
            }
            else if (_checksumType.StartsWith("sha256", StringComparison.OrdinalIgnoreCase))
            {
                SHA256.HashData(combined, hashed);
            }
            else
            {
                ulong h = FastXxHash64.Hash(combined);
                BinaryPrimitives.WriteUInt64LittleEndian(hashed, h);
            }

            for (int j = 0; j < _byteSize; j++)
            {
                _dXor[j] ^= hashed[j];
            }
        }

        public byte[] GetResult() => (byte[])_dXor.Clone();
    }
}
