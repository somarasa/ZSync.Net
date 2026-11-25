using System;
using ZstdSharp;
using ZstdSharp.Unsafe;

namespace ZSync.Core.Compression
{
    public sealed class ZstdBlockCompressor : IBlockCompressor
    {
        public static readonly ZstdBlockCompressor Instance = new();

        public string CompressType => "zstdD";

        public byte[] Compress(ReadOnlySpan<byte> input, int level = 3)
        {
            using var compressor = new Compressor(level);
            compressor.SetParameter(ZSTD_cParameter.ZSTD_c_experimentalParam2, 1); // ZSTD_f_zstd1_magicless
            return compressor.Wrap(input).ToArray();
        }

        public byte[] Decompress(ReadOnlySpan<byte> compressed, int uncompressedSize, byte[]? dict = null)
        {
            using var decompressor = new Decompressor();
            decompressor.SetParameter(ZSTD_dParameter.ZSTD_d_experimentalParam1, 1); // ZSTD_f_zstd1_magicless

            if (dict != null && dict.Length > 0)
            {
                decompressor.RefPrefix(dict);
            }

            var output = new byte[uncompressedSize];
            try
            {
                decompressor.UnwrapStream(compressed, output, out int consumed, out int written);
                if (written != uncompressedSize)
                {
                    Array.Resize(ref output, written);
                }
                return output;
            }
            catch (Exception ex)
            {
                string hex = BitConverter.ToString(compressed.Slice(0, Math.Min(compressed.Length, 16)).ToArray());
                throw new InvalidOperationException($"Decompress failed: compLen={compressed.Length}, uncompSize={uncompressedSize}, dictLen={dict?.Length ?? 0}, firstBytes={hex}. Ex: {ex.Message}", ex);
            }
        }
    }
}
