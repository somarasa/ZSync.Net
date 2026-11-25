using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using ZSync.Core.Compression;
using ZSync.Core.Hashing;
using ZSync.Core.Model;
using ZSync.Core.Transport;

namespace ZSync.Core.Engine
{
    public sealed class ZSyncProgressReport
    {
        public int TotalBlocks { get; init; }
        public int ProcessedBlocks { get; init; }
        public long LocalReusedBytes { get; init; }
        public long DownloadedBytes { get; init; }
        public long TotalBytes { get; init; }
        public double Percentage => TotalBlocks > 0 ? (double)ProcessedBlocks / TotalBlocks * 100.0 : 0.0;
    }

    public sealed class ZSyncResult
    {
        public string TargetPath { get; init; } = string.Empty;
        public long TotalBytes { get; init; }
        public long LocalReusedBytes { get; init; }
        public long DownloadedBytes { get; init; }
        public double BandwidthSavedRatio => TotalBytes > 0 ? (double)LocalReusedBytes / TotalBytes * 100.0 : 0.0;
        public ulong XxHash64 { get; init; }
        public string Sha256 { get; init; } = string.Empty;
    }

    public static class ZSyncSyncEngine
    {
        public static async Task<ZSyncResult> SyncAsync(
            ZSyncControlFile control,
            IByteRangeSource rangeSource,
            string destinationPath,
            string? localSourcePath = null,
            IProgress<ZSyncProgressReport>? progress = null,
            CancellationToken cancellationToken = default)
        {
            var destDir = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
            {
                Directory.CreateDirectory(destDir);
            }

            var effectiveSource = localSourcePath;
            if (string.IsNullOrEmpty(effectiveSource) && File.Exists(destinationPath))
            {
                effectiveSource = destinationPath;
            }

            // Assemble into a temporary sibling working file
            var workingTempPath = destinationPath + ".zsnew." + Guid.NewGuid().ToString("N")[..8];

            // 1. Match local existing files against control file
            MatchResult matchResult;
            if (!string.IsNullOrEmpty(effectiveSource) && File.Exists(effectiveSource))
            {
                matchResult = await ZSyncBlockMatcher.MatchLocalFileAsync(control, effectiveSource, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                matchResult = new MatchResult { MissingBlocks = new(control.Blocks) };
            }

            long localReusedBytes = 0;
            long downloadedBytes = 0;

            FileStream? localStream = null;
            if (!string.IsNullOrEmpty(effectiveSource) && File.Exists(effectiveSource) && matchResult.MatchedBlocks.Count > 0)
            {
                localStream = File.OpenRead(effectiveSource);
            }

            bool swapSuccess = false;

            try
            {
                var xxHashState = new FastXxHash64.State();
                using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                using var sha1 = (control.Format == ControlFileFormat.LegacyZSync && !string.IsNullOrEmpty(control.Sha1))
                    ? IncrementalHash.CreateHash(HashAlgorithmName.SHA1)
                    : null;
                var checkChecksum = (control.Format == ControlFileFormat.HDiffPatch && control.NewDataChecksum.Length > 0)
                    ? new HDiffPatchCheckChecksum(control.ChecksumType, control.NewDataChecksum.Length)
                    : null;

                using (var destStream = new FileStream(workingTempPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, bufferSize: 131072, useAsync: true))
                {
                    int totalBlocks = control.Blocks.Count;
                    int processedBlocks = 0;

                    var compressor = GetCompressor(control.CompressType);

                    foreach (var block in control.Blocks)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        byte[] blockData;
                        if (matchResult.MatchedBlocks.TryGetValue(block.BlockIndex, out var matched))
                        {
                            // Block found locally
                            localStream!.Seek(matched.SourceOffset, SeekOrigin.Begin);
                            blockData = new byte[matched.Length];
                            int read = 0;
                            while (read < matched.Length)
                            {
                                int r = await localStream.ReadAsync(blockData.AsMemory(read, matched.Length - read), cancellationToken).ConfigureAwait(false);
                                if (r == 0) break;
                                read += r;
                            }
                            await destStream.WriteAsync(blockData.AsMemory(0, read), cancellationToken).ConfigureAwait(false);

                            localReusedBytes += read;
                        }
                        else
                        {
                            // Block missing: download range from remote source
                            if (block.IsCompressed && compressor != null)
                            {
                                var compressedChunk = await rangeSource.ReadRangeAsync(
                                    block.CompressedOffset,
                                    block.CompressedLength,
                                    cancellationToken).ConfigureAwait(false);

                                downloadedBytes += compressedChunk.Length;

                                byte[]? dict = null;
                                if (destStream.Position > 0)
                                {
                                    long maxDict = control.DictSize > 0 ? (long)control.DictSize : 65536L;
                                    int dictLen = (int)Math.Min(destStream.Position, maxDict);
                                    dict = new byte[dictLen];
                                    long curPos = destStream.Position;
                                    await destStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                                    destStream.Seek(curPos - dictLen, SeekOrigin.Begin);
                                    int dRead = 0;
                                    while (dRead < dictLen)
                                    {
                                        int r = await destStream.ReadAsync(dict.AsMemory(dRead, dictLen - dRead), cancellationToken).ConfigureAwait(false);
                                        if (r == 0) break;
                                        dRead += r;
                                    }
                                    destStream.Seek(curPos, SeekOrigin.Begin);
                                }

                                blockData = compressor.Decompress(compressedChunk, block.UncompressedLength, dict);
                                await destStream.WriteAsync(blockData.AsMemory(), cancellationToken).ConfigureAwait(false);
                            }
                            else
                            {
                                long sourceOffset = control.Format == ControlFileFormat.HDiffPatch
                                    ? block.CompressedOffset
                                    : block.UncompressedOffset;

                                blockData = await rangeSource.ReadRangeAsync(
                                    sourceOffset,
                                    block.UncompressedLength,
                                    cancellationToken).ConfigureAwait(false);

                                downloadedBytes += blockData.Length;
                                await destStream.WriteAsync(blockData.AsMemory(), cancellationToken).ConfigureAwait(false);
                            }
                        }



                        // Feed streaming hashes
                        xxHashState.Update(blockData);
                        sha256.AppendData(blockData);
                        sha1?.AppendData(blockData);

                        if (checkChecksum != null)
                        {
                            byte[] blockStrong;
                            if (blockData.Length < control.BlockSize)
                            {
                                byte[] padded = new byte[control.BlockSize];
                                blockData.CopyTo(padded, 0);
                                blockStrong = ChecksumHelper.ComputeStrongChecksum(control.ChecksumType, padded);
                            }
                            else
                            {
                                blockStrong = ChecksumHelper.ComputeStrongChecksum(control.ChecksumType, blockData);
                            }
                            checkChecksum.AppendBlock(block.BlockIndex, blockStrong);
                        }

                        processedBlocks++;
                        progress?.Report(new ZSyncProgressReport
                        {
                            TotalBlocks = totalBlocks,
                            ProcessedBlocks = processedBlocks,
                            LocalReusedBytes = localReusedBytes,
                            DownloadedBytes = downloadedBytes,
                            TotalBytes = control.FileLength
                        });
                    }
                }

                // Verify file integrity
                if (checkChecksum != null)
                {
                    byte[] computedCheck = checkChecksum.GetResult();
                    if (!MemoryExtensions.SequenceEqual<byte>(computedCheck, control.NewDataChecksum))
                        throw new InvalidDataException("Verification failed: HDiffPatch check checksum mismatch on reconstructed file.");
                }
                if (sha1 != null)
                {
                    byte[] computedSha1 = sha1.GetHashAndReset();
                    string hex = Convert.ToHexString(computedSha1).ToLowerInvariant();
                    if (!string.Equals(hex, control.Sha1, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Verification failed: SHA-1 mismatch on reconstructed file.");
                }

                ulong fullXxHash = xxHashState.Digest();
                string fullSha256 = Convert.ToHexString(sha256.GetHashAndReset()).ToLowerInvariant();

                // Close local stream before moving
                localStream?.Dispose();
                localStream = null;

                // Atomic replace
                File.Move(workingTempPath, destinationPath, overwrite: true);
                swapSuccess = true;

                return new ZSyncResult
                {
                    TargetPath = destinationPath,
                    TotalBytes = control.FileLength,
                    LocalReusedBytes = localReusedBytes,
                    DownloadedBytes = downloadedBytes,
                    XxHash64 = fullXxHash,
                    Sha256 = fullSha256
                };
            }
            finally
            {
                localStream?.Dispose();
                if (!swapSuccess && File.Exists(workingTempPath))
                {
                    try { File.Delete(workingTempPath); } catch { }
                }
            }
        }

        private static IBlockCompressor? GetCompressor(string type)
        {
            return type switch
            {
                "zstd" or "zstdD" => ZstdBlockCompressor.Instance,
                "zlib" or "zlibD" or "ldef" or "gzip" or "gzipD" => DeflateBlockCompressor.Instance,
                _ => null
            };
        }
    }
}
