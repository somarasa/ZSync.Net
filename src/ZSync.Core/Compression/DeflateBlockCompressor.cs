using System;
using System.IO;
using System.IO.Compression;

namespace ZSync.Core.Compression
{
    public sealed class DeflateBlockCompressor : IBlockCompressor
    {
        public static readonly DeflateBlockCompressor Instance = new();

        public string CompressType => "zlib";

        public byte[] Compress(ReadOnlySpan<byte> input, int level = 3)
        {
            var compressionLevel = level >= 7 ? CompressionLevel.SmallestSize :
                                   level >= 3 ? CompressionLevel.Optimal : CompressionLevel.Fastest;
            using var ms = new MemoryStream();
            using (var deflate = new DeflateStream(ms, compressionLevel, leaveOpen: true))
            {
                deflate.Write(input);
            }
            return ms.ToArray();
        }

        public byte[] Decompress(ReadOnlySpan<byte> compressed, int uncompressedSize, byte[]? dict = null)
        {
            try
            {
                byte[] compBytes = compressed.ToArray();
                if (compBytes.Length >= 4 &&
                    compBytes[^4] == 0x00 && compBytes[^3] == 0x00 &&
                    compBytes[^2] == 0xFF && compBytes[^1] == 0xFF)
                {
                    Array.Resize(ref compBytes, compBytes.Length + 2);
                    compBytes[^2] = 0x03;
                    compBytes[^1] = 0x00;
                }

                using var inputMs = new MemoryStream(compBytes);
                using var deflate = new DeflateStream(inputMs, CompressionMode.Decompress);
                var output = new byte[uncompressedSize];
                int totalRead = 0;
                while (totalRead < uncompressedSize)
                {
                    int read = deflate.Read(output, totalRead, uncompressedSize - totalRead);
                    if (read == 0) break;
                    totalRead += read;
                }
                if (totalRead != uncompressedSize)
                {
                    Array.Resize(ref output, totalRead);
                }
                return output;
            }
            catch (Exception ex)
            {
                string hex = BitConverter.ToString(compressed.Slice(0, Math.Min(compressed.Length, 16)).ToArray());
                throw new InvalidOperationException($"Deflate decompress failed: compLen={compressed.Length}, uncompSize={uncompressedSize}, firstBytes={hex}. Ex: {ex.Message}", ex);
            }
        }
    }
}
