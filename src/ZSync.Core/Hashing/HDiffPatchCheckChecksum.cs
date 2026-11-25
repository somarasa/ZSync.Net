using System;
using System.Buffers.Binary;

namespace ZSync.Core.Hashing
{
    /// <summary>
    /// Computes the order-independent XOR-accumulated check checksum used in HDiffPatch HSyni/HSynz formats.
    /// Compatible with all HDiffPatch strong checksum plugins (xxh128, md5, sha256, sha512, crc32, etc.).
    /// </summary>
    public sealed class HDiffPatchCheckChecksum
    {
        private readonly byte[] _dXor;
        private readonly string _checksumType;
        private readonly int _byteSize;

        public HDiffPatchCheckChecksum(string checksumType, int byteSize)
        {
            _checksumType = checksumType;
            _byteSize = byteSize > 0 ? byteSize : ChecksumHelper.GetChecksumByteSize(checksumType);
            _dXor = new byte[_byteSize];
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
            blockChecksum.Slice(0, Math.Min(_byteSize, blockChecksum.Length)).CopyTo(combined.Slice(12, _byteSize));

            Span<byte> hashed = stackalloc byte[_byteSize];
            ChecksumHelper.ComputeStrongChecksum(_checksumType, combined, hashed);

            for (int j = 0; j < _byteSize; j++)
            {
                _dXor[j] ^= hashed[j];
            }
        }

        public byte[] GetResult() => (byte[])_dXor.Clone();
    }
}
