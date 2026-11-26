using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.IO.Hashing;
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
    /// Also supports single-pass dual-control Gzip generation for client delta sync and inter-site replication.
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
                    ? BinaryPrimitives.ReadUInt64LittleEndian(strongHashBytes)
                    : (ulong)BinaryPrimitives.ReadUInt32LittleEndian(strongHashBytes);

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

        /// <summary>
        /// Generates an RFC 1952 sync-friendly .gz payload, a client reconstruction control file (.hsyni),
        /// and an inter-site replication control file (.gz.hsyni) in a single streaming pass with O(1) memory.
        /// </summary>
        public static async Task<ZSyncDualGzipSummary> GenerateDualGzipAsync(
            string inputFilePath,
            string? outputDirectory = null,
            ZSyncDualGzipOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            if (!File.Exists(inputFilePath))
            {
                throw new FileNotFoundException($"Input file not found at '{inputFilePath}'.");
            }

            options ??= new ZSyncDualGzipOptions();
            if (options.BlockSize <= 0)
                throw new ArgumentOutOfRangeException(nameof(options.BlockSize), "BlockSize must be greater than zero.");
            if (options.GzBlockSize <= 0)
                throw new ArgumentOutOfRangeException(nameof(options.GzBlockSize), "GzBlockSize must be greater than zero.");

            var outDir = outputDirectory ?? Path.GetDirectoryName(inputFilePath) ?? Directory.GetCurrentDirectory();
            if (!Directory.Exists(outDir))
            {
                Directory.CreateDirectory(outDir);
            }

            var baseFileName = Path.GetFileName(inputFilePath);
            var gzPayloadPath = Path.Combine(outDir, baseFileName + ".gz");
            var clientControlPath = Path.Combine(outDir, baseFileName + (options.ClientFormat == ControlFileFormat.LegacyZSync ? ".zsync" : ".hsyni"));
            var interSiteControlPath = Path.Combine(outDir, baseFileName + ".gz.hsyni");

            using var inputStream = new FileStream(inputFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 131072, useAsync: true);
            using var outputGzFileStream = new FileStream(gzPayloadPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 131072, useAsync: true);

            var gzAccumulator = new GzBlockAccumulator(options.GzBlockSize, options.ChecksumType);
            using var gzTeeStream = new GzStreamTee(outputGzFileStream, gzAccumulator);

            int checksumByteSize = ChecksumHelper.GetChecksumByteSize(options.ChecksumType);
            int clientBlockSize = options.BlockSize;
            if (inputStream.Length > 0 && clientBlockSize > inputStream.Length)
            {
                clientBlockSize = (int)Math.Max(64, Math.Min((long)clientBlockSize, inputStream.Length));
            }

            var clientControl = new HSyniControlFile
            {
                CompressType = "gzipD",
                ChecksumType = options.ChecksumType,
                BlockSize = clientBlockSize,
                FileLength = inputStream.Length,
                ChecksumByteSize = checksumByteSize,
                SavedStrongChecksumBits = checksumByteSize * 8,
                SavedRollHashBits = 64,
                NewSyncDataOffset = 10 // 10-byte Gzip header
            };

            var clientXxHashState = new FastXxHash64.State();
            var clientCheckChecksum = new HDiffPatchCheckChecksum(options.ChecksumType, checksumByteSize);
            var crc32 = new Crc32();

            // 1. Write RFC 1952 10-byte GZip Header
            byte[] gzHead = new byte[] { 0x1F, 0x8B, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x03 };
            await gzTeeStream.WriteAsync(gzHead, cancellationToken).ConfigureAwait(false);
            await gzTeeStream.FlushAsync(cancellationToken).ConfigureAwait(false);

            long curCompOffset = 10;
            var defLevel = options.CompressionLevel >= 7 ? CompressionLevel.SmallestSize :
                           options.CompressionLevel >= 3 ? CompressionLevel.Optimal : CompressionLevel.Fastest;

            var buffer = new byte[clientBlockSize];
            int clientBlockIndex = 0;
            long uncompressedOffset = 0;
            int bytesRead;

            // 2. Compress uncompressed blocks and stream into .gz while teeing to gzAccumulator
            while ((bytesRead = await inputStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var blockSpan = buffer.AsSpan(0, bytesRead);
                bool isLastBlock = (uncompressedOffset + bytesRead >= inputStream.Length);

                clientXxHashState.Update(blockSpan);
                crc32.Append(blockSpan);

                ReadOnlySpan<byte> hashSpan = blockSpan;
                byte[]? zeroPadded = null;
                if (bytesRead < clientBlockSize)
                {
                    zeroPadded = new byte[clientBlockSize];
                    blockSpan.CopyTo(zeroPadded);
                    hashSpan = zeroPadded;
                }

                ulong weakHash = FastAdler64.Compute(hashSpan);
                byte[] strongHashBytes = ChecksumHelper.ComputeStrongChecksum(options.ChecksumType, hashSpan);
                ulong strongHash = strongHashBytes.Length >= 8
                    ? BinaryPrimitives.ReadUInt64LittleEndian(strongHashBytes)
                    : (ulong)BinaryPrimitives.ReadUInt32LittleEndian(strongHashBytes);

                clientCheckChecksum.AppendBlock(clientBlockIndex, strongHashBytes);

                byte[] compressedChunk;
                using (var blockMs = new MemoryStream())
                {
                    if (isLastBlock)
                    {
                        using (var def = new DeflateStream(blockMs, defLevel, leaveOpen: true))
                        {
                            def.Write(blockSpan);
                        }
                    }
                    else
                    {
                        var def = new DeflateStream(blockMs, defLevel, leaveOpen: true);
                        def.Write(blockSpan);
                        def.Flush();
                    }
                    compressedChunk = blockMs.ToArray();
                }

                await gzTeeStream.WriteAsync(compressedChunk, cancellationToken).ConfigureAwait(false);
                int compLen = compressedChunk.Length;

                clientControl.Blocks.Add(new BlockChecksum
                {
                    BlockIndex = clientBlockIndex,
                    UncompressedOffset = uncompressedOffset,
                    UncompressedLength = bytesRead,
                    WeakHash = weakHash,
                    StrongHash = strongHash,
                    StrongHashBytes = strongHashBytes,
                    IsCompressed = true,
                    CompressedOffset = curCompOffset,
                    CompressedLength = compLen
                });

                curCompOffset += compLen;
                uncompressedOffset += bytesRead;
                clientBlockIndex++;
            }

            if (uncompressedOffset == 0)
            {
                // Empty file: write 2-byte empty deflate stream (0x03, 0x00)
                byte[] emptyDeflate = new byte[] { 0x03, 0x00 };
                await gzTeeStream.WriteAsync(emptyDeflate, cancellationToken).ConfigureAwait(false);
            }

            // 4. Write RFC 1952 8-byte GZip Footer: CRC32 (4 bytes LE) + ISIZE (4 bytes LE)
            uint crcValue = crc32.GetCurrentHashAsUInt32();
            byte[] footer = new byte[8];
            BinaryPrimitives.WriteUInt32LittleEndian(footer.AsSpan(0, 4), crcValue);
            BinaryPrimitives.WriteUInt32LittleEndian(footer.AsSpan(4, 4), (uint)(inputStream.Length & 0xFFFFFFFF));
            await gzTeeStream.WriteAsync(footer, cancellationToken).ConfigureAwait(false);
            await gzTeeStream.FlushAsync(cancellationToken).ConfigureAwait(false);

            long totalGzSize = outputGzFileStream.Position;
            clientControl.NewSyncDataSize = totalGzSize;
            clientControl.NewDataChecksum = clientCheckChecksum.GetResult();

            // 5. Finalize Inter-Site Control (.gz.hsyni)
            var interSiteControl = gzAccumulator.Finish(out ulong gzXxHash);

            // 6. Save Client Control File
            if (options.ClientFormat == ControlFileFormat.LegacyZSync)
            {
                throw new NotSupportedException("Legacy .zsync format does not support gzipD compressed payload ranges; HDiffPatch .hsyni format is required.");
            }

            using (var clientFs = File.Create(clientControlPath))
            {
                await clientControl.SaveAsync(clientFs, cancellationToken).ConfigureAwait(false);
            }

            // 7. Save Inter-Site Control File
            using (var interSiteFs = File.Create(interSiteControlPath))
            {
                await interSiteControl.SaveAsync(interSiteFs, cancellationToken).ConfigureAwait(false);
            }

            return new ZSyncDualGzipSummary
            {
                SourcePath = inputFilePath,
                GzPayloadPath = gzPayloadPath,
                ClientControlPath = clientControlPath,
                InterSiteControlPath = interSiteControlPath,
                UncompressedSize = uncompressedOffset,
                GzPayloadSize = totalGzSize,
                ClientBlockCount = clientBlockIndex,
                GzBlockCount = interSiteControl.Blocks.Count,
                UncompressedXxHash64 = clientXxHashState.Digest(),
                GzXxHash64 = gzXxHash,
                Crc32 = crcValue
            };
        }

        private sealed class GzBlockAccumulator
        {
            private readonly byte[] _buffer;
            private int _bufferCount;
            private long _totalBytes;
            private int _blockIndex;
            private readonly string _checksumType;
            private readonly int _checksumByteSize;
            private readonly HDiffPatchCheckChecksum _checkChecksum;
            private readonly FastXxHash64.State _xxHashState;
            private readonly List<BlockChecksum> _blocks;

            public GzBlockAccumulator(int gzBlockSize, string checksumType)
            {
                _buffer = new byte[gzBlockSize];
                _checksumType = checksumType;
                _checksumByteSize = ChecksumHelper.GetChecksumByteSize(checksumType);
                _checkChecksum = new HDiffPatchCheckChecksum(checksumType, _checksumByteSize);
                _xxHashState = new FastXxHash64.State();
                _blocks = new List<BlockChecksum>();
            }

            public void Append(ReadOnlySpan<byte> span)
            {
                while (span.Length > 0)
                {
                    int toCopy = Math.Min(span.Length, _buffer.Length - _bufferCount);
                    span.Slice(0, toCopy).CopyTo(_buffer.AsSpan(_bufferCount));
                    _bufferCount += toCopy;
                    _totalBytes += toCopy;
                    span = span.Slice(toCopy);

                    if (_bufferCount == _buffer.Length)
                    {
                        FlushFullBlock();
                    }
                }
            }

            private void FlushFullBlock()
            {
                _xxHashState.Update(_buffer);
                ulong weakHash = FastAdler64.Compute(_buffer);
                byte[] strongHashBytes = ChecksumHelper.ComputeStrongChecksum(_checksumType, _buffer);
                ulong strongHash = strongHashBytes.Length >= 8
                    ? BinaryPrimitives.ReadUInt64LittleEndian(strongHashBytes)
                    : (ulong)BinaryPrimitives.ReadUInt32LittleEndian(strongHashBytes);

                _checkChecksum.AppendBlock(_blockIndex, strongHashBytes);

                _blocks.Add(new BlockChecksum
                {
                    BlockIndex = _blockIndex,
                    UncompressedOffset = (long)_blockIndex * _buffer.Length,
                    UncompressedLength = _buffer.Length,
                    WeakHash = weakHash,
                    StrongHash = strongHash,
                    StrongHashBytes = strongHashBytes,
                    IsCompressed = false,
                    CompressedOffset = (long)_blockIndex * _buffer.Length,
                    CompressedLength = _buffer.Length
                });

                _blockIndex++;
                _bufferCount = 0;
            }

            public HSyniControlFile Finish(out ulong xxHash64)
            {
                int effectiveGzBlockSize = _buffer.Length;
                if (_totalBytes > 0 && effectiveGzBlockSize > _totalBytes)
                {
                    effectiveGzBlockSize = (int)Math.Max(64, Math.Min((long)effectiveGzBlockSize, _totalBytes));
                }

                if (_bufferCount > 0)
                {
                    _xxHashState.Update(_buffer.AsSpan(0, _bufferCount));
                    byte[] padded = new byte[effectiveGzBlockSize];
                    Array.Copy(_buffer, padded, _bufferCount);

                    ulong weakHash = FastAdler64.Compute(padded);
                    byte[] strongHashBytes = ChecksumHelper.ComputeStrongChecksum(_checksumType, padded);
                    ulong strongHash = strongHashBytes.Length >= 8
                        ? BinaryPrimitives.ReadUInt64LittleEndian(strongHashBytes)
                        : (ulong)BinaryPrimitives.ReadUInt32LittleEndian(strongHashBytes);

                    _checkChecksum.AppendBlock(_blockIndex, strongHashBytes);

                    _blocks.Add(new BlockChecksum
                    {
                        BlockIndex = _blockIndex,
                        UncompressedOffset = (long)_blockIndex * _buffer.Length,
                        UncompressedLength = _bufferCount,
                        WeakHash = weakHash,
                        StrongHash = strongHash,
                        StrongHashBytes = strongHashBytes,
                        IsCompressed = false,
                        CompressedOffset = (long)_blockIndex * _buffer.Length,
                        CompressedLength = _bufferCount
                    });

                    _blockIndex++;
                    _bufferCount = 0;
                }

                xxHash64 = _xxHashState.Digest();

                return new HSyniControlFile
                {
                    CompressType = "",
                    ChecksumType = _checksumType,
                    BlockSize = effectiveGzBlockSize,
                    FileLength = _totalBytes,
                    NewSyncDataSize = _totalBytes,
                    NewSyncDataOffset = 0,
                    ChecksumByteSize = _checksumByteSize,
                    SavedStrongChecksumBits = _checksumByteSize * 8,
                    SavedRollHashBits = 64,
                    NewDataChecksum = _checkChecksum.GetResult(),
                    Blocks = _blocks
                };
            }
        }

        private sealed class GzStreamTee : Stream
        {
            private readonly Stream _underlying;
            private readonly GzBlockAccumulator _accumulator;

            public GzStreamTee(Stream underlying, GzBlockAccumulator accumulator)
            {
                _underlying = underlying;
                _accumulator = accumulator;
            }

            public override bool CanRead => false;
            public override bool CanSeek => _underlying.CanSeek;
            public override bool CanWrite => _underlying.CanWrite;
            public override long Length => _underlying.Length;
            public override long Position
            {
                get => _underlying.Position;
                set => _underlying.Position = value;
            }

            public override void Flush() => _underlying.Flush();
            public override Task FlushAsync(CancellationToken cancellationToken) => _underlying.FlushAsync(cancellationToken);

            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => _underlying.Seek(offset, origin);
            public override void SetLength(long value) => _underlying.SetLength(value);

            public override void Write(byte[] buffer, int offset, int count)
            {
                _underlying.Write(buffer, offset, count);
                _accumulator.Append(buffer.AsSpan(offset, count));
            }

            public override void Write(ReadOnlySpan<byte> buffer)
            {
                _underlying.Write(buffer);
                _accumulator.Append(buffer);
            }

            public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                await _underlying.WriteAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
                _accumulator.Append(buffer.AsSpan(offset, count));
            }

            public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                await _underlying.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
                _accumulator.Append(buffer.Span);
            }
        }
    }
}
