using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ZSync.Core.Compression;
using ZSync.Core.Hashing;
using ZSync.Core.Model;

namespace ZSync.Core.Format.HDiffPatch
{
    public sealed class HSyniControlFile
    {
        public const string MagicFile = "HSyni23";
        public const string MagicPayload = "HSynz23";

        public string CompressType { get; set; } = "zstd";
        public string ChecksumType { get; set; } = "xxh64";
        public ulong DictSize { get; set; } = 0;
        public long FileLength { get; set; }
        public long NewSyncDataSize { get; set; }
        public long NewSyncDataOffset { get; set; }
        public int BlockSize { get; set; } = 4096;
        public int ChecksumByteSize { get; set; } = 8;
        public int SavedStrongChecksumBits { get; set; } = 64;
        public int SavedRollHashBits { get; set; } = 64;
        public byte[] NewDataChecksum { get; set; } = Array.Empty<byte>();

        public List<BlockChecksum> Blocks { get; set; } = new();

        public static bool IsHSyni(ReadOnlySpan<byte> header)
        {
            return header.Length >= 7 && Encoding.ASCII.GetString(header.Slice(0, 7)) == MagicFile;
        }

        public static async Task<HSyniControlFile> LoadAsync(Stream stream, CancellationToken cancellationToken = default)
        {
            var headerBytes = ReadNullTerminatedString(stream);
            var parts = headerBytes.Split('&');
            if (parts.Length < 3 || parts[0] != MagicFile)
                throw new InvalidDataException($"Invalid HSyni header: '{headerBytes}'");

            var control = new HSyniControlFile
            {
                CompressType = parts[1],
                ChecksumType = parts[2]
            };

            control.DictSize = PackUInt.ReadUInt(stream);
            ulong decompressInfoSize = PackUInt.ReadUInt(stream);
            if (decompressInfoSize > 0)
            {
                byte[] decompressInfo = new byte[decompressInfoSize];
                await ReadExactAsync(stream, decompressInfo, cancellationToken).ConfigureAwait(false);
            }

            control.NewSyncDataSize = (long)PackUInt.ReadUInt(stream);
            control.NewSyncDataOffset = (long)PackUInt.ReadUInt(stream);
            control.FileLength = (long)PackUInt.ReadUInt(stream);
            control.BlockSize = (int)PackUInt.ReadUInt(stream);
            control.ChecksumByteSize = (int)PackUInt.ReadUInt(stream);
            control.SavedStrongChecksumBits = (int)PackUInt.ReadUInt(stream);
            control.SavedRollHashBits = (int)PackUInt.ReadUInt(stream);
            ulong samePairCount = PackUInt.ReadUInt(stream);
            byte isSavedSizes = (byte)stream.ReadByte();
            ulong privateExternDataSize = PackUInt.ReadUInt(stream);
            ulong externDataSize = PackUInt.ReadUInt(stream);
            ulong uncompressDataSize = PackUInt.ReadUInt(stream);
            ulong compressDataSize = PackUInt.ReadUInt(stream);
            byte isDirSyncInfo = (byte)stream.ReadByte();
            ulong newSyncInfoSize = PackUInt.ReadUInt(stream);

            int blockCount = (int)((control.FileLength + control.BlockSize - 1) / control.BlockSize);

            // Read newDataCheckChecksum
            control.NewDataChecksum = new byte[control.ChecksumByteSize];
            await ReadExactAsync(stream, control.NewDataChecksum, cancellationToken).ConfigureAwait(false);

            // Read control buffer (same pairs and saved sizes)
            byte[] ctrlBuf;
            if (compressDataSize > 0)
            {
                byte[] compressedCtrl = new byte[compressDataSize];
                await ReadExactAsync(stream, compressedCtrl, cancellationToken).ConfigureAwait(false);
                var compressor = GetCompressor(control.CompressType);
                ctrlBuf = compressor.Decompress(compressedCtrl, (int)uncompressDataSize);
            }
            else
            {
                ctrlBuf = new byte[uncompressDataSize];
                await ReadExactAsync(stream, ctrlBuf, cancellationToken).ConfigureAwait(false);
            }

            // Parse same pairs and saved sizes from ctrlBuf
            using var ctrlMs = new MemoryStream(ctrlBuf);
            var samePairs = new Dictionary<int, int>();
            uint pre = 0;
            for (ulong i = 0; i < samePairCount; i++)
            {
                uint curDelta = (uint)PackUInt.ReadUInt(ctrlMs);
                uint sameDelta = (uint)PackUInt.ReadUInt(ctrlMs);
                uint curIndex = pre + curDelta;
                uint sameIndex = curIndex - sameDelta;
                samePairs[(int)curIndex] = (int)sameIndex;
                pre = curIndex;
            }

            uint[] savedSizes = new uint[blockCount];
            if (isSavedSizes != 0)
            {
                uint backSize = 0;
                for (int i = 0; i < blockCount; i++)
                {
                    uint diff = (uint)PackUInt.ReadUIntWithTag(ctrlMs, 1, out byte highTag);
                    if (highTag == 0)
                    {
                        backSize += diff;
                    }
                    else if (diff > 0)
                    {
                        backSize -= diff;
                    }
                    else
                    {
                        backSize = (uint)control.BlockSize;
                    }
                    savedSizes[i] = backSize;
                }
            }

            // Read infoChecksum
            byte[] infoChecksum = new byte[control.ChecksumByteSize];
            await ReadExactAsync(stream, infoChecksum, cancellationToken).ConfigureAwait(false);

            // Read partHash (weak rolling hashes and strong hashes)
            int rollBytes = (control.SavedRollHashBits + 7) / 8;
            byte[] rollHashBytes = ReadPartHashes(stream, blockCount, control.SavedRollHashBits, samePairs);

            int strongBytes = (control.SavedStrongChecksumBits + 7) / 8;
            byte[] strongHashBytes = ReadPartHashes(stream, blockCount, control.SavedStrongChecksumBits, samePairs);

            // Read infoPartHashChecksum
            byte[] infoPartHashChecksum = new byte[control.ChecksumByteSize];
            await ReadExactAsync(stream, infoPartHashChecksum, cancellationToken).ConfigureAwait(false);

            // Construct blocks
            control.Blocks = new List<BlockChecksum>(blockCount);
            long uncompressedOffset = 0;
            long compressedOffset = control.NewSyncDataOffset;

            for (int i = 0; i < blockCount; i++)
            {
                int uncompressedLen = (int)Math.Min((long)control.BlockSize, control.FileLength - uncompressedOffset);
                ulong weakHash = ReadRollHashBytes(rollHashBytes.AsSpan(i * rollBytes, rollBytes));
                ulong strongHash = ReadStrongHashBytes(strongHashBytes.AsSpan(i * strongBytes, strongBytes));

                int compressedLen = isSavedSizes != 0 ? (int)savedSizes[i] : uncompressedLen;
                bool isCompressed = isSavedSizes != 0 && compressedLen > 0;

                control.Blocks.Add(new BlockChecksum
                {
                    BlockIndex = i,
                    UncompressedOffset = uncompressedOffset,
                    UncompressedLength = uncompressedLen,
                    WeakHash = weakHash,
                    StrongHash = strongHash,
                    IsCompressed = isCompressed,
                    CompressedOffset = compressedOffset,
                    CompressedLength = compressedLen
                });

                uncompressedOffset += uncompressedLen;
                compressedOffset += compressedLen;
            }

            return control;
        }

        public async Task SaveAsync(Stream stream, CancellationToken cancellationToken = default)
        {
            int blockCount = Blocks.Count;
            int kStrongChecksumByteSize = ChecksumByteSize;

            var samePairList = new List<(uint CurIndex, uint SameIndex)>();
            // Deduplicate if needed
            var hashToFirst = new Dictionary<ulong, uint>();
            for (int i = 0; i < blockCount; i++)
            {
                var b = Blocks[i];
                if (hashToFirst.TryGetValue(b.StrongHash, out uint firstIndex))
                {
                    samePairList.Add(((uint)i, firstIndex));
                }
                else
                {
                    hashToFirst[b.StrongHash] = (uint)i;
                }
            }

            // Encode samePairList and savedSizes into ctrlMs
            using var ctrlMs = new MemoryStream();
            uint pre = 0;
            foreach (var pair in samePairList)
            {
                PackUInt.WriteUInt(ctrlMs, pair.CurIndex - pre);
                PackUInt.WriteUInt(ctrlMs, pair.CurIndex - pair.SameIndex);
                pre = pair.CurIndex;
            }

            bool hasSavedSizes = CompressType != "";
            if (hasSavedSizes)
            {
                uint backSize = 0;
                for (int i = 0; i < blockCount; i++)
                {
                    uint sz = (uint)Blocks[i].CompressedLength;
                    if (sz != 0)
                    {
                        if (sz >= backSize)
                            PackUInt.WriteUIntWithTag(ctrlMs, sz - backSize, highTag: 0, tagBit: 1);
                        else
                            PackUInt.WriteUIntWithTag(ctrlMs, backSize - sz, highTag: 1, tagBit: 1);
                        backSize = sz;
                    }
                    else
                    {
                        PackUInt.WriteUIntWithTag(ctrlMs, 0, highTag: 1, tagBit: 1);
                        backSize = (uint)BlockSize;
                    }
                }
            }

            byte[] uncompressedCtrl = ctrlMs.ToArray();
            byte[] finalCtrlBuf = uncompressedCtrl;
            ulong compressDataSize = 0;

            if (uncompressedCtrl.Length > 0 && CompressType != "")
            {
                var compressor = GetCompressor(CompressType);
                byte[] cm = compressor.Compress(uncompressedCtrl);
                if (cm.Length < uncompressedCtrl.Length)
                {
                    finalCtrlBuf = cm;
                    compressDataSize = (ulong)cm.Length;
                }
            }

            ulong uncompressDataSize = (ulong)uncompressedCtrl.Length;

            // Generate part hashes
            int matchBlockCount = blockCount - samePairList.Count;
            byte[] rollPartBytes = WritePartHashes(Blocks, SavedRollHashBits, samePairList, isRollHash: true);
            byte[] strongPartBytes = WritePartHashes(Blocks, SavedStrongChecksumBits, samePairList, isRollHash: false);

            // Compute head and overall size
            using var headMs = new MemoryStream();
            byte[] typeBytes = Encoding.ASCII.GetBytes($"{MagicFile}&{CompressType}&{ChecksumType}\0");
            headMs.Write(typeBytes);

            PackUInt.WriteUInt(headMs, DictSize);
            PackUInt.WriteUInt(headMs, 0); // decompressInfoSize
            PackUInt.WriteUInt(headMs, (ulong)NewSyncDataSize);
            PackUInt.WriteUInt(headMs, (ulong)NewSyncDataOffset);
            PackUInt.WriteUInt(headMs, (ulong)FileLength);
            PackUInt.WriteUInt(headMs, (ulong)BlockSize);
            PackUInt.WriteUInt(headMs, (ulong)kStrongChecksumByteSize);
            PackUInt.WriteUInt(headMs, (ulong)SavedStrongChecksumBits);
            PackUInt.WriteUInt(headMs, (ulong)SavedRollHashBits);
            PackUInt.WriteUInt(headMs, (ulong)samePairList.Count);
            headMs.WriteByte((byte)(hasSavedSizes ? 1 : 0));
            PackUInt.WriteUInt(headMs, 0); // privateExternDataSize
            PackUInt.WriteUInt(headMs, 0); // externDataSize
            PackUInt.WriteUInt(headMs, uncompressDataSize);
            PackUInt.WriteUInt(headMs, compressDataSize);
            headMs.WriteByte(0); // isDirSyncInfo

            // Total newSyncInfoSize calculation
            ulong estimatedSize = (ulong)headMs.Length + (ulong)kStrongChecksumByteSize + (ulong)finalCtrlBuf.Length
                                  + (ulong)kStrongChecksumByteSize // infoChecksum
                                  + (ulong)rollPartBytes.Length
                                  + (ulong)strongPartBytes.Length
                                  + (ulong)kStrongChecksumByteSize; // infoPartHashChecksum

            int pkSize = PackUInt.GetPackedSize(estimatedSize);
            ulong finalInfoSize = estimatedSize + (ulong)pkSize;
            if (PackUInt.GetPackedSize(finalInfoSize) > pkSize)
            {
                finalInfoSize++;
            }
            PackUInt.WriteUInt(headMs, finalInfoSize);

            // Stream everything out
            byte[] headBytes = headMs.ToArray();
            await stream.WriteAsync(headBytes, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(NewDataChecksum, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(finalCtrlBuf, cancellationToken).ConfigureAwait(false);

            // Info checksum (hash of head + newDataChecksum + finalCtrlBuf)
            byte[] infoChecksumData = ComputeStrongChecksum(headBytes, NewDataChecksum, finalCtrlBuf);
            await stream.WriteAsync(infoChecksumData, cancellationToken).ConfigureAwait(false);

            // Part hashes
            await stream.WriteAsync(rollPartBytes, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(strongPartBytes, cancellationToken).ConfigureAwait(false);

            // Info part hash checksum (hash of infoChecksum + rollPartBytes + strongPartBytes)
            byte[] infoPartHashChecksum = ComputeStrongChecksum(infoChecksumData, rollPartBytes, strongPartBytes);
            await stream.WriteAsync(infoPartHashChecksum, cancellationToken).ConfigureAwait(false);
        }

        private static byte[] ReadPartHashes(Stream stream, int blockCount, int partBits, Dictionary<int, int> samePairs)
        {
            int partBytes = (partBits + 7) / 8;
            byte[] result = new byte[blockCount * partBytes];
            int highBit = (partBits & 7) != 0 ? (partBits & 7) : 8;

            ulong bitsValue = 0;
            int bitsCount = 0;

            for (int i = 0; i < blockCount; i++)
            {
                int outOffset = i * partBytes;
                if (samePairs.TryGetValue(i, out int sameIndex))
                {
                    Array.Copy(result, sameIndex * partBytes, result, outOffset, partBytes);
                }
                else
                {
                    for (int j = 0; j < partBytes; j++)
                    {
                        if (bitsCount < 8)
                        {
                            int b = stream.ReadByte();
                            if (b < 0) throw new EndOfStreamException();
                            bitsValue |= ((ulong)(byte)b) << bitsCount;
                            bitsCount += 8;
                        }

                        if (j == 0)
                        {
                            result[outOffset + j] = (byte)(bitsValue & ((1UL << highBit) - 1UL));
                            bitsValue >>= highBit;
                            bitsCount -= highBit;
                        }
                        else
                        {
                            result[outOffset + j] = (byte)bitsValue;
                            bitsValue >>= 8;
                            bitsCount -= 8;
                        }
                    }
                }
            }

            return result;
        }

        private static byte[] WritePartHashes(List<BlockChecksum> blocks, int partBits, List<(uint CurIndex, uint SameIndex)> samePairs, bool isRollHash)
        {
            int blockCount = blocks.Count;
            int partBytes = (partBits + 7) / 8;
            int highBit = (partBits & 7) != 0 ? (partBits & 7) : 8;

            var sameDict = new HashSet<uint>();
            foreach (var p in samePairs) sameDict.Add(p.CurIndex);

            using var ms = new MemoryStream();
            ulong bitsValue = 0;
            int bitsCount = 0;

            byte[] tempBuf = new byte[partBytes];

            for (int i = 0; i < blockCount; i++)
            {
                if (sameDict.Contains((uint)i))
                    continue;

                var b = blocks[i];
                if (isRollHash)
                {
                    WriteRollHashBytes(tempBuf.AsSpan(0, partBytes), b.WeakHash, partBytes);
                }
                else
                {
                    if (b.StrongHashBytes != null && b.StrongHashBytes.Length >= partBytes)
                    {
                        b.StrongHashBytes.AsSpan(0, partBytes).CopyTo(tempBuf);
                    }
                    else
                    {
                        BinaryPrimitives.WriteUInt64LittleEndian(tempBuf, b.StrongHash);
                    }
                    if (partBytes * 8 > partBits)
                        tempBuf[0] &= (byte)((1 << (partBits & 7)) - 1);
                }

                for (int j = 0; j < partBytes; j++)
                {
                    int vbit = j == 0 ? highBit : 8;
                    bitsValue |= ((ulong)tempBuf[j]) << bitsCount;
                    bitsCount += vbit;
                    while (bitsCount >= 8)
                    {
                        ms.WriteByte((byte)bitsValue);
                        bitsValue >>= 8;
                        bitsCount -= 8;
                    }
                }
            }

            if (bitsCount > 0)
            {
                ms.WriteByte((byte)bitsValue);
            }

            return ms.ToArray();
        }

        private static void WriteRollHashBytes(Span<byte> outPart, ulong partRollHash, int savedBytes)
        {
            for (int i = savedBytes - 1; i >= 0; i--)
            {
                outPart[savedBytes - 1 - i] = (byte)(partRollHash >> (i * 8));
            }
        }

        private static ulong ReadRollHashBytes(ReadOnlySpan<byte> part)
        {
            ulong hash = 0;
            for (int i = 0; i < part.Length; i++)
            {
                hash = (hash << 8) | part[i];
            }
            return hash;
        }

        private static ulong ReadStrongHashBytes(ReadOnlySpan<byte> part)
        {
            if (part.Length >= 8)
                return BinaryPrimitives.ReadUInt64LittleEndian(part);
            ulong v = 0;
            for (int i = 0; i < part.Length; i++)
                v |= ((ulong)part[i]) << (i * 8);
            return v;
        }

        private byte[] ComputeStrongChecksum(params byte[][] buffers)
        {
            using var ms = new MemoryStream();
            foreach (var b in buffers)
                ms.Write(b, 0, b.Length);
            byte[] all = ms.ToArray();

            if (ChecksumType.StartsWith("md5", StringComparison.OrdinalIgnoreCase))
            {
                return MD5.HashData(all);
            }
            else if (ChecksumType.StartsWith("sha256", StringComparison.OrdinalIgnoreCase))
            {
                return SHA256.HashData(all);
            }
            else
            {
                byte[] result = new byte[ChecksumByteSize];
                ulong hash = FastXxHash64.Hash(all);
                BinaryPrimitives.WriteUInt64LittleEndian(result, hash);
                return result;
            }
        }

        private static IBlockCompressor GetCompressor(string type)
        {
            return type switch
            {
                "zstd" or "zstdD" => ZstdBlockCompressor.Instance,
                "zlib" or "zlibD" or "ldef" or "gzip" or "gzipD" => DeflateBlockCompressor.Instance,
                _ => throw excitingNotSupported(type)
            };

            static NotSupportedException excitingNotSupported(string t) =>
                new($"Unsupported HDiffPatch compression type: '{t}'.");
        }

        private static string ReadNullTerminatedString(Stream stream)
        {
            var sb = new StringBuilder();
            int b;
            while ((b = stream.ReadByte()) > 0)
            {
                sb.Append((char)b);
            }
            if (b < 0 && sb.Length == 0)
                throw new EndOfStreamException();
            return sb.ToString();
        }

        private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
        {
            int total = 0;
            while (total < buffer.Length)
            {
                int read = await stream.ReadAsync(buffer, total, buffer.Length - total, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    throw new EndOfStreamException($"Expected {buffer.Length} bytes but reached EOF after {total} bytes.");
                total += read;
            }
        }
    }
}
