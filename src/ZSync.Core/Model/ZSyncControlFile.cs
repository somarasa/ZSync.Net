using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ZSync.Core.Format.HDiffPatch;
using ZSync.Core.Format.ZSyncLegacy;

namespace ZSync.Core.Model
{
    public enum ControlFileFormat
    {
        HDiffPatch,
        LegacyZSync
    }

    public sealed class ZSyncControlFile
    {
        public ControlFileFormat Format { get; set; } = ControlFileFormat.HDiffPatch;
        public string FileName { get; set; } = string.Empty;
        public long FileLength { get; set; }
        public long CompressedFileLength { get; set; }
        public int BlockSize { get; set; } = 4096;
        public bool IsGzipCompressed { get; set; }
        public string CompressType { get; set; } = "zstd";
        public string ChecksumType { get; set; } = "xxh64";
        public string? PayloadUrl { get; set; }
        public string Sha256 { get; set; } = string.Empty;
        public string Sha1 { get; set; } = string.Empty;
        public ulong DictSize { get; set; }
        public int SavedRollHashBits { get; set; } = 64;
        public int SavedStrongChecksumBits { get; set; } = 64;
        public ulong XxHash64 { get; set; }
        public byte[] NewDataChecksum { get; set; } = Array.Empty<byte>();

        public List<BlockChecksum> Blocks { get; set; } = new();

        public async Task SaveAsync(Stream stream, CancellationToken cancellationToken = default)
        {
            if (Format == ControlFileFormat.HDiffPatch)
            {
                var hsyni = new HSyniControlFile
                {
                    CompressType = CompressType,
                    ChecksumType = ChecksumType,
                    BlockSize = BlockSize,
                    FileLength = FileLength,
                    NewSyncDataSize = CompressedFileLength,
                    NewSyncDataOffset = 0,
                    NewDataChecksum = NewDataChecksum,
                    Blocks = Blocks
                };
                await hsyni.SaveAsync(stream, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var legacy = new LegacyZSyncControlFile
                {
                    FileName = FileName,
                    FileLength = FileLength,
                    BlockSize = BlockSize,
                    Url = PayloadUrl,
                    Blocks = Blocks
                };
                await legacy.SaveAsync(stream, cancellationToken).ConfigureAwait(false);
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
            if (!stream.CanSeek)
            {
                var ms = new MemoryStream();
                await stream.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
                ms.Position = 0;
                return await LoadAsync(ms, cancellationToken).ConfigureAwait(false);
            }

            long startPos = stream.Position;
            byte[] sniffBuf = new byte[16];
            int read = await stream.ReadAsync(sniffBuf, 0, sniffBuf.Length, cancellationToken).ConfigureAwait(false);
            stream.Position = startPos;

            if (HSyniControlFile.IsHSyni(sniffBuf.AsSpan(0, read)))
            {
                var hsyni = await HSyniControlFile.LoadAsync(stream, cancellationToken).ConfigureAwait(false);
                return new ZSyncControlFile
                {
                    Format = ControlFileFormat.HDiffPatch,
                    BlockSize = hsyni.BlockSize,
                    FileLength = hsyni.FileLength,
                    CompressedFileLength = hsyni.NewSyncDataSize,
                    CompressType = hsyni.CompressType,
                    ChecksumType = hsyni.ChecksumType,
                    DictSize = hsyni.DictSize,
                    SavedRollHashBits = hsyni.SavedRollHashBits,
                    SavedStrongChecksumBits = hsyni.SavedStrongChecksumBits,
                    NewDataChecksum = hsyni.NewDataChecksum,
                    Blocks = hsyni.Blocks
                };
            }
            else if (LegacyZSyncControlFile.IsLegacyZSync(sniffBuf.AsSpan(0, read)))
            {
                var legacy = await LegacyZSyncControlFile.LoadAsync(stream, cancellationToken).ConfigureAwait(false);
                return new ZSyncControlFile
                {
                    Format = ControlFileFormat.LegacyZSync,
                    FileName = legacy.FileName,
                    BlockSize = legacy.BlockSize,
                    FileLength = legacy.FileLength,
                    CompressedFileLength = legacy.FileLength,
                    PayloadUrl = legacy.Url,
                    Sha1 = legacy.Sha1 ?? string.Empty,
                    Blocks = legacy.Blocks
                };
            }
            else
            {
                throw new InvalidDataException("Unrecognized control file format: expected HDiffPatch (.hsyni) or legacy zsync (.zsync).");
            }
        }

        public static async Task<ZSyncControlFile> LoadAsync(string filePath, CancellationToken cancellationToken = default)
        {
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException($"ZSync control file not found at '{filePath}'.");
            }

            using var fs = File.OpenRead(filePath);
            var control = await LoadAsync(fs, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(control.FileName))
            {
                control.FileName = Path.GetFileNameWithoutExtension(filePath);
            }
            return control;
        }
    }
}
