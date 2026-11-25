using System;

namespace ZSync.Core.Model
{
    public sealed class BlockChecksum
    {
        public int BlockIndex { get; set; }
        public long UncompressedOffset { get; set; }
        public int UncompressedLength { get; set; }
        public ulong WeakHash { get; set; }
        public ulong StrongHash { get; set; }
        public byte[]? StrongHashBytes { get; set; }

        public bool IsCompressed { get; set; }
        public long CompressedOffset { get; set; }
        public int CompressedLength { get; set; }
    }
}
