using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ZSync.Core.Model
{
    public sealed class ZSyncControlFile
    {
        public static readonly byte[] Magic = Encoding.ASCII.GetBytes("ZSYNC_NET_V1");

        public string FileName { get; set; } = string.Empty;
        public long FileLength { get; set; }
        public long CompressedFileLength { get; set; }
        public int BlockSize { get; set; } = 4096;
        public bool IsGzipCompressed { get; set; }
        public string? PayloadUrl { get; set; }
        public string Sha256 { get; set; } = string.Empty;
        public ulong XxHash64 { get; set; }

        public List<BlockChecksum> Blocks { get; set; } = new();

        public async Task SaveAsync(Stream stream, CancellationToken cancellationToken = default)
        {
            await stream.WriteAsync(Magic, 0, Magic.Length, cancellationToken).ConfigureAwait(false);

            using var bw = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
            bw.Write(FileName ?? string.Empty);
            bw.Write(FileLength);
            bw.Write(CompressedFileLength);
            bw.Write(BlockSize);
            bw.Write(IsGzipCompressed);
            bw.Write(PayloadUrl ?? string.Empty);
            bw.Write(Sha256 ?? string.Empty);
            bw.Write(XxHash64);

            bw.Write(Blocks.Count);
            foreach (var b in Blocks)
            {
                bw.Write(b.BlockIndex);
                bw.Write(b.UncompressedOffset);
                bw.Write(b.UncompressedLength);
                bw.Write(b.WeakHash);
                bw.Write(b.StrongHash);
                bw.Write(b.IsCompressed);
                bw.Write(b.CompressedOffset);
                bw.Write(b.CompressedLength);
            }
        }

        public async Task SaveAsync(string filePath, CancellationToken cancellationToken = default)
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            using var fs = File.Create(filePath);
            await SaveAsync(fs, cancellationToken).ConfigureAwait(false);
        }

        public static async Task<ZSyncControlFile> LoadAsync(Stream stream, CancellationToken cancellationToken = default)
        {
            var magicBuf = new byte[Magic.Length];
            int read = await stream.ReadAsync(magicBuf, 0, magicBuf.Length, cancellationToken).ConfigureAwait(false);
            if (read < Magic.Length || !MemoryExtensions.SequenceEqual<byte>(magicBuf, Magic))
            {
                throw new InvalidDataException("Invalid ZSync control file: magic header mismatch.");
            }

            using var br = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            var control = new ZSyncControlFile
            {
                FileName = br.ReadString(),
                FileLength = br.ReadInt64(),
                CompressedFileLength = br.ReadInt64(),
                BlockSize = br.ReadInt32(),
                IsGzipCompressed = br.ReadBoolean(),
                PayloadUrl = br.ReadString(),
                Sha256 = br.ReadString(),
                XxHash64 = br.ReadUInt64()
            };

            int count = br.ReadInt32();
            control.Blocks = new List<BlockChecksum>(count);

            for (int i = 0; i < count; i++)
            {
                control.Blocks.Add(new BlockChecksum
                {
                    BlockIndex = br.ReadInt32(),
                    UncompressedOffset = br.ReadInt64(),
                    UncompressedLength = br.ReadInt32(),
                    WeakHash = br.ReadUInt32(),
                    StrongHash = br.ReadUInt64(),
                    IsCompressed = br.ReadBoolean(),
                    CompressedOffset = br.ReadInt64(),
                    CompressedLength = br.ReadInt32()
                });
            }

            return control;
        }

        public static async Task<ZSyncControlFile> LoadAsync(string filePath, CancellationToken cancellationToken = default)
        {
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException($"ZSync control file not found at '{filePath}'.");
            }

            using var fs = File.OpenRead(filePath);
            return await LoadAsync(fs, cancellationToken).ConfigureAwait(false);
        }
    }
}
