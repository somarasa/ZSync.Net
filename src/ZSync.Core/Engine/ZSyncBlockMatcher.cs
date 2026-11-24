using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

            var weakLookup = new Dictionary<uint, List<BlockChecksum>>();
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

            var localBytes = await File.ReadAllBytesAsync(localFilePath, cancellationToken).ConfigureAwait(false);
            if (localBytes.Length >= blockSize)
            {
                var roller = new RollingHash(blockSize);
                int limit = localBytes.Length - blockSize;
                int pos = 0;

                roller.Compute(localBytes.AsSpan(0, blockSize));

                while (pos <= limit)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    uint currentWeak = roller.Value;
                    if (weakLookup.TryGetValue(currentWeak, out var candidates) && candidates.Count > 0)
                    {
                        var windowSpan = localBytes.AsSpan(pos, blockSize);
                        ulong currentStrong = FastXxHash64.Hash(windowSpan);

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
                                SourceOffset = pos,
                                Length = blockSize
                            };

                            // Advance by full block size
                            pos += blockSize;
                            if (pos <= limit)
                            {
                                roller.Compute(localBytes.AsSpan(pos, blockSize));
                            }
                            continue;
                        }
                    }

                    // Roll forward by 1 byte
                    if (pos < limit)
                    {
                        byte oldByte = localBytes[pos];
                        byte newByte = localBytes[pos + blockSize];
                        roller.Roll(oldByte, newByte);
                    }
                    pos++;
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
    }
}
