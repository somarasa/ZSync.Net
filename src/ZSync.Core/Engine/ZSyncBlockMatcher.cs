using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using ZSync.Core.Hashing;
using ZSync.Core.Model;

namespace ZSync.Core.Engine
{
    public sealed class MatchedBlock
    {
        public BlockChecksum TargetBlock { get; init; } = null!;
        public string SourceFilePath { get; init; } = string.Empty;
        public long SourceOffset { get; init; }
        public int Length { get; init; }
    }

    public sealed class MatchResult
    {
        public Dictionary<int, MatchedBlock> MatchedBlocks { get; init; } = new();
        public List<BlockChecksum> MissingBlocks { get; init; } = new();
        public long MatchedBytes => MatchedBlocks.Values.Sum(m => (long)m.Length);
        public long MissingBytes => MissingBlocks.Sum(b => (long)b.UncompressedLength);
    }

    public static class ZSyncBlockMatcher
    {
        public static async Task<MatchResult> MatchLocalFileAsync(
            ZSyncControlFile control,
            string localFilePath,
            CancellationToken cancellationToken = default)
        {
            if (!File.Exists(localFilePath))
            {
                return new MatchResult
                {
                    MissingBlocks = new List<BlockChecksum>(control.Blocks)
                };
            }

            var weakLookup = new Dictionary<ulong, List<BlockChecksum>>();
            foreach (var block in control.Blocks)
            {
                if (!weakLookup.TryGetValue(block.WeakHash, out var list))
                {
                    list = new List<BlockChecksum>();
                    weakLookup[block.WeakHash] = list;
                }
                list.Add(block);
            }

            var matched = new Dictionary<int, MatchedBlock>();
            int blockSize = control.BlockSize;

            using (var stream = new FileStream(localFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 131072, useAsync: true))
            {
                if (stream.Length >= blockSize)
                {
                    bool isHDiff = control.Format == ControlFileFormat.HDiffPatch;
                    FastAdler64 fadler = isHDiff ? new FastAdler64(blockSize) : default;
                    RollingHash rsum = !isHDiff ? new RollingHash(blockSize) : default;

                    const int bufferChunkSize = 1024 * 1024; // 1 MB streaming window
                    byte[] buffer = new byte[bufferChunkSize + blockSize];
                    int bytesInBuffer = 0;
                    long streamBaseOffset = 0;
                    bool initialized = false;

                    while (true)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        int toRead = buffer.Length - bytesInBuffer;
                        int read = await stream.ReadAsync(buffer.AsMemory(bytesInBuffer, toRead), cancellationToken).ConfigureAwait(false);
                        bytesInBuffer += read;

                        if (bytesInBuffer < blockSize)
                            break;

                        int pos = 0;
                        int limit = bytesInBuffer - blockSize;

                        if (!initialized)
                        {
                            if (isHDiff)
                                fadler.Init(buffer.AsSpan(0, blockSize));
                            else
                                rsum.Compute(buffer.AsSpan(0, blockSize));
                            initialized = true;
                        }

                        while (pos <= limit)
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            ulong currentWeak = isHDiff
                                ? FastAdler64.ToSavedPartRollHash(fadler.Value, control.SavedRollHashBits)
                                : rsum.Value;

                            if (weakLookup.TryGetValue(currentWeak, out var candidates) && candidates.Count > 0)
                            {
                                var windowSpan = buffer.AsSpan(pos, blockSize);
                                ulong currentStrong = ComputeStrongHash(control.ChecksumType, windowSpan, control.SavedStrongChecksumBits);

                                BlockChecksum? found = null;
                                for (int i = 0; i < candidates.Count; i++)
                                {
                                    var c = candidates[i];
                                    if (c.StrongHash == currentStrong && !matched.ContainsKey(c.BlockIndex))
                                    {
                                        found = c;
                                        candidates.RemoveAt(i);
                                        break;
                                    }
                                }

                                if (found != null)
                                {
                                    matched[found.BlockIndex] = new MatchedBlock
                                    {
                                        TargetBlock = found,
                                        SourceFilePath = localFilePath,
                                        SourceOffset = streamBaseOffset + pos,
                                        Length = blockSize
                                    };

                                    // Advance by full block size
                                    pos += blockSize;
                                    if (pos <= limit)
                                    {
                                        if (isHDiff)
                                            fadler.Init(buffer.AsSpan(pos, blockSize));
                                        else
                                            rsum.Compute(buffer.AsSpan(pos, blockSize));
                                    }
                                    continue;
                                }
                            }

                            // Roll forward by 1 byte
                            if (pos < limit)
                            {
                                byte oldByte = buffer[pos];
                                byte newByte = buffer[pos + blockSize];
                                if (isHDiff)
                                    fadler.Roll(oldByte, newByte);
                                else
                                    rsum.Roll(oldByte, newByte);
                            }
                            pos++;
                        }

                        int remaining = bytesInBuffer - pos;
                        if (remaining > 0)
                        {
                            Buffer.BlockCopy(buffer, pos, buffer, 0, remaining);
                            bytesInBuffer = remaining;
                            streamBaseOffset += pos;
                            initialized = false;
                        }
                        else
                        {
                            int excess = pos - bytesInBuffer;
                            streamBaseOffset += pos;
                            bytesInBuffer = 0;
                            initialized = false;
                            if (excess > 0)
                            {
                                stream.Seek(excess, SeekOrigin.Current);
                            }
                        }

                        if (read == 0)
                            break;
                    }
                }
            }

            var missing = control.Blocks
                .Where(b => !matched.ContainsKey(b.BlockIndex))
                .ToList();

            return new MatchResult
            {
                MatchedBlocks = matched,
                MissingBlocks = missing
            };
        }

        private static ulong ComputeStrongHash(string checksumType, ReadOnlySpan<byte> window, int savedStrongChecksumBits = 64)
        {
            byte[] strong = ChecksumHelper.ComputeStrongChecksum(checksumType, window);
            return ToPartChecksum(strong, savedStrongChecksumBits);
        }

        private static ulong ToPartChecksum(ReadOnlySpan<byte> checksum, int outPartBits)
        {
            if (outPartBits <= 0) return 0;
            int outPartBytes = (outPartBits + 7) / 8;
            Span<byte> partBuf = stackalloc byte[outPartBytes];
            checksum.Slice(0, Math.Min(outPartBytes, checksum.Length)).CopyTo(partBuf);
            if (outPartBytes * 8 > outPartBits)
            {
                partBuf[0] &= (byte)((1 << (outPartBits & 7)) - 1);
            }
            if (outPartBytes >= 8)
            {
                return BinaryPrimitives.ReadUInt64LittleEndian(partBuf);
            }
            ulong v = 0;
            for (int i = 0; i < outPartBytes; i++)
            {
                v |= ((ulong)partBuf[i]) << (i * 8);
            }
            return v;
        }
    }
}
