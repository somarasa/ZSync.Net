using System;

namespace ZSync.Core.Model
{
    public sealed class BlockChecksum
    {
        public int BlockIndex { get; init; }
        public long UncompressedOffset { get; init; }
        public int UncompressedLength { get; init; }
        public uint WeakHash { get; init; }
        public ulong StrongHash { get; init; } // FastXxHash64

        // GZip compressed block metadata (HDiffPatch/zsync gzip feature)
        public bool IsCompressed { get; init; }
        public long CompressedOffset { get; init; }
        public int CompressedLength { get; init; }
    }
}
