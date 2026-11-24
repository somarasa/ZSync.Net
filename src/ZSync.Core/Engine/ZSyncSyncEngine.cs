using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using ZSync.Core.Hashing;
using ZSync.Core.Model;
using ZSync.Core.Transport;

namespace ZSync.Core.Engine
{
    public sealed class ZSyncResult
    {
        public string DestinationPath { get; init; } = string.Empty;
        public long TotalBytes { get; init; }
        public long LocalReusedBytes { get; init; }
        public long DownloadedBytes { get; init; }
        public double BandwidthSavingsPercentage => TotalBytes > 0 ? (double)LocalReusedBytes / TotalBytes * 100.0 : 0.0;
        public bool ChecksumVerified { get; init; }
    }

    public static class ZSyncSyncEngine
    {
        public static async Task<ZSyncResult> SyncAsync(
            ZSyncControlFile control,
            IByteRangeSource rangeSource,
            string? localSourceFile,
            string destinationPath,
            IProgress<double>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (control == null) throw new ArgumentNullException(nameof(control));
            if (rangeSource == null) throw new ArgumentNullException(nameof(rangeSource));
            if (string.IsNullOrWhiteSpace(destinationPath)) throw new ArgumentNullException(nameof(destinationPath));

            var destDir = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
            {
                Directory.CreateDirectory(destDir);
            }

            // 1. Match local existing files against control file
            MatchResult matchResult;
            if (!string.IsNullOrEmpty(localSourceFile) && File.Exists(localSourceFile))
            {
                matchResult = await ZSyncBlockMatcher.MatchLocalFileAsync(control, localSourceFile, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                matchResult = new MatchResult { MissingBlocks = new(control.Blocks) };
            }

            long localReusedBytes = 0;
            long downloadedBytes = 0;

            FileStream? localStream = null;
            if (!string.IsNullOrEmpty(localSourceFile) && File.Exists(localSourceFile) && matchResult.MatchedBlocks.Count > 0)
            {
                localStream = File.OpenRead(localSourceFile);
            }

            try
            {
                using var destStream = File.Create(destinationPath);

                int totalBlocks = control.Blocks.Count;
                int processedBlocks = 0;

                foreach (var block in control.Blocks)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (matchResult.MatchedBlocks.TryGetValue(block.BlockIndex, out var matched))
                    {
                        // Block found locally in base file!
                        localStream!.Seek(matched.SourceOffset, SeekOrigin.Begin);
                        var buffer = new byte[matched.Length];
                        int read = await localStream.ReadAsync(buffer.AsMemory(0, matched.Length), cancellationToken).ConfigureAwait(false);
                        await destStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);

                        localReusedBytes += read;
                    }
                    else
                    {
                        // Block missing locally: download range from remote/SMB source
                        if (control.IsGzipCompressed)
                        {
                            var compressedChunk = await rangeSource.ReadRangeAsync(
                                block.CompressedOffset,
                                block.CompressedLength,
                                cancellationToken).ConfigureAwait(false);

                            downloadedBytes += compressedChunk.Length;

                            // Decompress on the fly
                            using var chunkMs = new MemoryStream(compressedChunk);
                            using var deflate = new DeflateStream(chunkMs, CompressionMode.Decompress);
                            await deflate.CopyToAsync(destStream, cancellationToken).ConfigureAwait(false);
                        }
                        else
                        {
                            var rawChunk = await rangeSource.ReadRangeAsync(
                                block.UncompressedOffset,
                                block.UncompressedLength,
                                cancellationToken).ConfigureAwait(false);

                            downloadedBytes += rawChunk.Length;
                            await destStream.WriteAsync(rawChunk, cancellationToken).ConfigureAwait(false);
                        }
                    }

                    processedBlocks++;
                    progress?.Report((double)processedBlocks / totalBlocks * 100.0);
                }

                await destStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                localStream?.Dispose();
            }

            // 2. Verify assembled file checksums
            bool verified = false;
            using (var destVerifyStream = File.OpenRead(destinationPath))
            {
                using var sha256 = SHA256.Create();
                var hashBytes = await sha256.ComputeHashAsync(destVerifyStream, cancellationToken).ConfigureAwait(false);
                var sha = BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();

                verified = string.Equals(sha, control.Sha256, StringComparison.OrdinalIgnoreCase);
            }

            return new ZSyncResult
            {
                DestinationPath = destinationPath,
                TotalBytes = control.FileLength,
                LocalReusedBytes = localReusedBytes,
                DownloadedBytes = downloadedBytes,
                ChecksumVerified = verified
            };
        }
    }
}
