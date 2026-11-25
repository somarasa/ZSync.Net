using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using ZSync.Core.Compression;
using ZSync.Core.Format.HDiffPatch;
using ZSync.Core.Hashing;
using ZSync.Core.Model;

namespace ZSync.Core.Engine
{
    public sealed class ZSyncControlBuilderOptions
    {
        public int BlockSize { get; set; } = 4096;
        public string CompressType { get; set; } = "zstd"; // "zstd", "zlib", or ""
        public string ChecksumType { get; set; } = "xxh64";
        public int CompressionLevel { get; set; } = 3;
    }

    public sealed class ZSyncGenerationSummary
    {
        public string ControlPath { get; set; } = string.Empty;
        public string PayloadPath { get; set; } = string.Empty;
        public long UncompressedSize { get; set; }
        public long CompressedSize { get; set; }
        public int BlockCount { get; set; }
        public ulong XxHash64 { get; set; }
        public double CompressionRatio => UncompressedSize > 0 ? (double)CompressedSize / UncompressedSize * 100.0 : 0.0;
    }

    /// <summary>
    /// Builds HDiffPatch-compatible .hsyni control files and .hsynz payloads in a single streaming pass.
    /// </summary>
    public static class ZSyncControlBuilder
    {
        public static async Task<ZSyncGenerationSummary> GenerateAllAsync(
            string inputFilePath,
            string? outputDirectory = null,
            ZSyncControlBuilderOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            if (!File.Exists(inputFilePath))
            {
                throw new FileNotFoundException($"Input file not found at '{inputFilePath}'.");
            }

            options ??= new ZSyncControlBuilderOptions();
            var outDir = outputDirectory ?? Path.GetDirectoryName(inputFilePath) ?? Directory.GetCurrentDirectory();
            if (!Directory.Exists(outDir))
            {
                Directory.CreateDirectory(outDir);
            }

            var baseFileName = Path.GetFileName(inputFilePath);
            var hsyniPath = Path.Combine(outDir, baseFileName + ".hsyni");
            var hsynzPath = Path.Combine(outDir, baseFileName + ".hsynz");

            using var inputStream = new FileStream(inputFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 131072, useAsync: true);
            using var outputHsynzStream = new FileStream(hsynzPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 131072, useAsync: true);

            string normCompressType = options.CompressType switch
            {
                "zstd" or "zstdD" => "zstdD",
                "zlib" or "zlibD" or "ldef" => "zlibD",
                "gzip" or "gzipD" => "gzipD",
                _ => string.Empty
            };

            int checksumByteSize = ChecksumHelper.GetChecksumByteSize(options.ChecksumType);
            var control = new HSyniControlFile
            {
                CompressType = normCompressType,
                ChecksumType = options.ChecksumType,
                BlockSize = options.BlockSize,
                FileLength = inputStream.Length,
                ChecksumByteSize = checksumByteSize,
                SavedStrongChecksumBits = checksumByteSize * 8,
                SavedRollHashBits = 64
            };

            // Write .hsynz head: HSynz23&<compressType>&<strongChecksumType>\0, packUInt(newDataSize), packUInt(kSyncBlockSize), packUInt(checksumByteSize)
            byte[] hsynzHeadType = System.Text.Encoding.ASCII.GetBytes($"{HSyniControlFile.MagicPayload}&{normCompressType}&{options.ChecksumType}\0");
            await outputHsynzStream.WriteAsync(hsynzHeadType, cancellationToken).ConfigureAwait(false);
            PackUInt.WriteUInt(outputHsynzStream, (ulong)inputStream.Length);
            PackUInt.WriteUInt(outputHsynzStream, (ulong)options.BlockSize);
            PackUInt.WriteUInt(outputHsynzStream, (ulong)control.ChecksumByteSize);

            long hsynzDataOffset = outputHsynzStream.Position;
            control.NewSyncDataOffset = hsynzDataOffset;

            IBlockCompressor? compressor = normCompressType switch
            {
                "zstdD" => ZstdBlockCompressor.Instance,
                "zlibD" or "gzipD" => DeflateBlockCompressor.Instance,
                _ => null
            };

            var buffer = new byte[options.BlockSize];
            int blockIndex = 0;
            long uncompressedOffset = 0;
            long compressedOffset = hsynzDataOffset;
            int bytesRead;

            var xxHashState = new FastXxHash64.State();
            var checkChecksum = new HDiffPatchCheckChecksum(options.ChecksumType, control.ChecksumByteSize);

            while ((bytesRead = await inputStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var blockSpan = buffer.AsSpan(0, bytesRead);

                xxHashState.Update(blockSpan);

                ReadOnlySpan<byte> hashSpan = blockSpan;
                byte[]? zeroPadded = null;
                if (bytesRead < options.BlockSize)
                {
                    zeroPadded = new byte[options.BlockSize];
                    blockSpan.CopyTo(zeroPadded);
                    hashSpan = zeroPadded;
                }

                ulong weakHash = FastAdler64.Compute(hashSpan);
                byte[] strongHashBytes = ChecksumHelper.ComputeStrongChecksum(options.ChecksumType, hashSpan);
                ulong strongHash = strongHashBytes.Length >= 8
                    ? System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(strongHashBytes)
                    : (ulong)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(strongHashBytes);

                checkChecksum.AppendBlock(blockIndex, strongHashBytes);

                byte[]? compressedChunk = compressor?.Compress(blockSpan, options.CompressionLevel);
                bool isCompressed = compressedChunk != null && compressedChunk.Length < bytesRead;
                int savedSize = isCompressed ? compressedChunk!.Length : 0;
                int payloadLen = isCompressed ? compressedChunk!.Length : bytesRead;

                if (isCompressed)
                {
                    await outputHsynzStream.WriteAsync(compressedChunk!, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await outputHsynzStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
                }

                control.Blocks.Add(new BlockChecksum
                {
                    BlockIndex = blockIndex,
                    UncompressedOffset = uncompressedOffset,
                    UncompressedLength = bytesRead,
                    WeakHash = weakHash,
                    StrongHash = strongHash,
                    StrongHashBytes = strongHashBytes,
                    IsCompressed = isCompressed,
                    CompressedOffset = compressedOffset,
                    CompressedLength = savedSize
                });

                blockIndex++;
                uncompressedOffset += bytesRead;
                compressedOffset += payloadLen;
            }

            // Compute whole file checksum
            ulong fullXxHash = xxHashState.Digest();
            byte[] fileChecksumBytes = checkChecksum.GetResult();

            // Write .hsynz foot: newDataCheckChecksum
            await outputHsynzStream.WriteAsync(fileChecksumBytes, cancellationToken).ConfigureAwait(false);
            long totalHsynzSize = outputHsynzStream.Position;

            control.NewSyncDataSize = totalHsynzSize;
            control.NewDataChecksum = fileChecksumBytes;

            // Write .hsyni
            using (var hsyniStream = File.Create(hsyniPath))
            {
                await control.SaveAsync(hsyniStream, cancellationToken).ConfigureAwait(false);
            }

            return new ZSyncGenerationSummary
            {
                ControlPath = hsyniPath,
                PayloadPath = hsynzPath,
                UncompressedSize = uncompressedOffset,
                CompressedSize = totalHsynzSize,
                BlockCount = blockIndex,
                XxHash64 = fullXxHash
            };
        }
    }
}
