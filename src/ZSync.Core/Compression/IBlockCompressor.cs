using System;
using System.IO;

namespace ZSync.Core.Compression
{
    public interface IBlockCompressor
    {
        string CompressType { get; }
        byte[] Compress(ReadOnlySpan<byte> input, int level = 3);
        byte[] Decompress(ReadOnlySpan<byte> compressed, int uncompressedSize, byte[]? dict = null);
    }
}
