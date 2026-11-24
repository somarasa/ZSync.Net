using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using ZSync.Core.Hashing;
using ZSync.Core.Model;

namespace ZSync.Core.Engine
{
    public sealed class ZSyncControlBuilderOptions
    {
        public int BlockSize { get; set; } = 4096;
        public CompressionLevel CompressionLevel { get; set; } = CompressionLevel.Optimal;
        public string? PayloadUrl { get; set; }
        public string? GzipPayloadUrl { get; set; }
    }

    public sealed class ZSyncGenerationSummary
    {
        public string UncompressedControlPath { get; set; } = string.Empty;
        public string GzipPayloadPath { get; set; } = string.Empty;
        public string GzipControlPath { get; set; } = string.Empty;
        public long UncompressedSize { get; set; }
        public long GzipSize { get; set; }
        public int BlockCount { get; set; }
        public string Sha256 { get; set; } = string.Empty;
        public ulong XxHash64 { get; set; }
        public double CompressionRatio => UncompressedSize > 0 ? (double)GzipSize / UncompressedSize * 100.0 : 0.0;
    }

    /// <summary>
    /// Builds ZSync control files (.zsync) and generates block-compressed GZip payloads (.gz).
    /// Capable of generating uncompressed control, compressed control, and gzip file in a single streaming pass to minimize disk I/O.
    /// </summary>
    public static class ZSyncControlBuilder
    {
        /// <summary>
        /// Generates the uncompressed .zsync file, the block-compressed .gz file, and the .gz.zsync file all at once in a single streaming pass to minimize disk I/O.
        /// </summary>
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
            var uncompressedControlPath = Path.Combine(outDir, baseFileName + ".zsync");
            var gzipPayloadPath = Path.Combine(outDir, baseFileName + ".gz");
            var gzipControlPath = Path.Combine(outDir, baseFileName + ".gz.zsync");

            using var inputStream = new FileStream(inputFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 131072, useAsync: true);
            using var outputGzStream = new FileStream(gzipPayloadPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 131072, useAsync: true);

            var uncompressedControl = new ZSyncControlFile
            {
                FileName = baseFileName,
                BlockSize = options.BlockSize,
                IsGzipCompressed = false,
                PayloadUrl = options.PayloadUrl ?? baseFileName
            };

            var gzipControl = new ZSyncControlFile
            {
                FileName = baseFileName,
                BlockSize = options.BlockSize,
                IsGzipCompressed = true,
                PayloadUrl = options.GzipPayloadUrl ?? (baseFileName + ".gz")
            };

            using var sha256 = SHA256.Create();
            using var wholeFileHasherStream = new MemoryStream();

            var buffer = new byte[options.BlockSize];
            int blockIndex = 0;
            long uncompressedOffset = 0;
            long compressedOffset = 0;
            int bytesRead;

            while ((bytesRead = await inputStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var blockSpan = buffer.AsSpan(0, bytesRead);

                // Compute weak Adler32 and strong xxHash64 on uncompressed block
                uint weakHash = RollingHash.Calculate(blockSpan);
                ulong strongHash = FastXxHash64.Hash(blockSpan);

                // 1. Add to uncompressed control
                uncompressedControl.Blocks.Add(new BlockChecksum
                {
                    BlockIndex = blockIndex,
                    UncompressedOffset = uncompressedOffset,
                    UncompressedLength = bytesRead,
                    WeakHash = weakHash,
                    StrongHash = strongHash,
                    IsCompressed = false,
                    CompressedOffset = 0,
                    CompressedLength = 0
                });

                // 2. Compress block for GZip payload (reuses in-memory stream buffer)
                using var blockMs = new MemoryStream();
                using (var deflate = new DeflateStream(blockMs, options.CompressionLevel, leaveOpen: true))
                {
                    await deflate.WriteAsync(buffer, 0, bytesRead, cancellationToken).ConfigureAwait(false);
                }

                var compressedBytes = blockMs.ToArray();
                await outputGzStream.WriteAsync(compressedBytes, cancellationToken).ConfigureAwait(false);

                // 3. Add to gzip control (mapping uncompressed hashes to compressed offsets)
                gzipControl.Blocks.Add(new BlockChecksum
                {
                    BlockIndex = blockIndex,
                    UncompressedOffset = uncompressedOffset,
                    UncompressedLength = bytesRead,
                    WeakHash = weakHash,
                    StrongHash = strongHash,
                    IsCompressed = true,
                    CompressedOffset = compressedOffset,
                    CompressedLength = compressedBytes.Length
                });

                blockIndex++;
                sha256.TransformBlock(buffer, 0, bytesRead, null, 0);
                wholeFileHasherStream.Write(buffer, 0, bytesRead);

                uncompressedOffset += bytesRead;
                compressedOffset += compressedBytes.Length;
            }

            sha256.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            var shaHex = BitConverter.ToString(sha256.Hash!).Replace("-", "").ToLowerInvariant();
            var xxHash = FastXxHash64.Hash(wholeFileHasherStream.ToArray());

            uncompressedControl.FileLength = uncompressedOffset;
            uncompressedControl.CompressedFileLength = 0;
            uncompressedControl.Sha256 = shaHex;
            uncompressedControl.XxHash64 = xxHash;

            gzipControl.FileLength = uncompressedOffset;
            gzipControl.CompressedFileLength = compressedOffset;
            gzipControl.Sha256 = shaHex;
            gzipControl.XxHash64 = xxHash;

            // Save both control files
            await uncompressedControl.SaveAsync(uncompressedControlPath, cancellationToken).ConfigureAwait(false);
            await gzipControl.SaveAsync(gzipControlPath, cancellationToken).ConfigureAwait(false);

            return new ZSyncGenerationSummary
            {
                UncompressedControlPath = uncompressedControlPath,
                GzipPayloadPath = gzipPayloadPath,
                GzipControlPath = gzipControlPath,
                UncompressedSize = uncompressedOffset,
                GzipSize = compressedOffset,
                BlockCount = blockIndex,
                Sha256 = shaHex,
                XxHash64 = xxHash
            };
        }

        /// <summary>
        /// Generates a single ZSync control file (with or without gzip compression).
        /// </summary>
        public static async Task<ZSyncControlFile> BuildAsync(
            string inputFilePath,
            string outputControlPath,
            string? outputGzipPath = null,
            ZSyncControlBuilderOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            if (!File.Exists(inputFilePath))
            {
                throw new FileNotFoundException($"Input file not found at '{inputFilePath}'.");
            }

            options ??= new ZSyncControlBuilderOptions();
            var enableGzip = !string.IsNullOrWhiteSpace(outputGzipPath);

            using var inputStream = new FileStream(inputFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 131072, useAsync: true);
            Stream? outputGzStream = null;
            if (enableGzip && !string.IsNullOrWhiteSpace(outputGzipPath))
            {
                var dir = Path.GetDirectoryName(outputGzipPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                outputGzStream = new FileStream(outputGzipPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 131072, useAsync: true);
            }

            try
            {
                var control = new ZSyncControlFile
                {
                    FileName = Path.GetFileName(inputFilePath),
                    BlockSize = options.BlockSize,
                    IsGzipCompressed = enableGzip && outputGzStream != null,
                    PayloadUrl = options.PayloadUrl
                };

                using var sha256 = SHA256.Create();
                using var wholeFileHasherStream = new MemoryStream();

                var buffer = new byte[options.BlockSize];
                int blockIndex = 0;
                long uncompressedOffset = 0;
                long compressedOffset = 0;
                int bytesRead;

                while ((bytesRead = await inputStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var blockSpan = buffer.AsSpan(0, bytesRead);

                    uint weakHash = RollingHash.Calculate(blockSpan);
                    ulong strongHash = FastXxHash64.Hash(blockSpan);

                    bool isCompressed = false;
                    int compressedLength = 0;

                    if (control.IsGzipCompressed && outputGzStream != null)
                    {
                        using var blockMs = new MemoryStream();
                        using (var deflate = new DeflateStream(blockMs, options.CompressionLevel, leaveOpen: true))
                        {
                            await deflate.WriteAsync(buffer, 0, bytesRead, cancellationToken).ConfigureAwait(false);
                        }

                        var compressedBytes = blockMs.ToArray();
                        isCompressed = true;
                        compressedLength = compressedBytes.Length;
                        await outputGzStream.WriteAsync(compressedBytes, cancellationToken).ConfigureAwait(false);
                    }

                    control.Blocks.Add(new BlockChecksum
                    {
                        BlockIndex = blockIndex++,
                        UncompressedOffset = uncompressedOffset,
                        UncompressedLength = bytesRead,
                        WeakHash = weakHash,
                        StrongHash = strongHash,
                        IsCompressed = isCompressed,
                        CompressedOffset = compressedOffset,
                        CompressedLength = compressedLength
                    });

                    sha256.TransformBlock(buffer, 0, bytesRead, null, 0);
                    wholeFileHasherStream.Write(buffer, 0, bytesRead);

                    uncompressedOffset += bytesRead;
                    compressedOffset += compressedLength;
                }

                sha256.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                control.Sha256 = BitConverter.ToString(sha256.Hash!).Replace("-", "").ToLowerInvariant();
                control.XxHash64 = FastXxHash64.Hash(wholeFileHasherStream.ToArray());
                control.FileLength = uncompressedOffset;
                control.CompressedFileLength = compressedOffset;

                await control.SaveAsync(outputControlPath, cancellationToken).ConfigureAwait(false);
                return control;
            }
            finally
            {
                if (outputGzStream != null)
                {
                    await outputGzStream.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }
}
