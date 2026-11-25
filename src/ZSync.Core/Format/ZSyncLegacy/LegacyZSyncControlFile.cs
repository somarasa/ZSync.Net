using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ZSync.Core.Hashing;
using ZSync.Core.Model;

namespace ZSync.Core.Format.ZSyncLegacy
{
    /// <summary>
    /// Reads and writes legacy Colin Phipps zsync control files (.zsync).
    /// </summary>
    public sealed class LegacyZSyncControlFile
    {
        public string Version { get; set; } = "0.6.2";
        public string FileName { get; set; } = string.Empty;
        public long FileLength { get; set; }
        public int BlockSize { get; set; } = 4096;
        public int SeqMatches { get; set; } = 1;
        public int RollHashBytes { get; set; } = 2;
        public int StrongHashBytes { get; set; } = 4;
        public string? Url { get; set; }
        public string? Sha1 { get; set; }
        public string? MTime { get; set; }

        public List<BlockChecksum> Blocks { get; set; } = new();

        public static bool IsLegacyZSync(ReadOnlySpan<byte> header)
        {
            return header.Length >= 6 && Encoding.ASCII.GetString(header.Slice(0, 6)) == "zsync:";
        }

        public static async Task<LegacyZSyncControlFile> LoadAsync(Stream stream, CancellationToken cancellationToken = default)
        {
            var control = new LegacyZSyncControlFile();
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // 1. Read ASCII MIME headers until double newline (empty line)
            // We read line-by-line directly from the stream byte-by-byte to avoid buffering past the headers.
            string? line;
            while ((line = await ReadAsciiLineAsync(stream, cancellationToken).ConfigureAwait(false)) != null)
            {
                if (string.IsNullOrWhiteSpace(line))
                    break; // Header section ended

                int colon = line.IndexOf(':');
                if (colon > 0)
                {
                    string key = line.Substring(0, colon).Trim();
                    string value = line.Substring(colon + 1).Trim();
                    headers[key] = value;
                }
            }

            if (headers.TryGetValue("zsync", out var ver)) control.Version = ver;
            if (headers.TryGetValue("Filename", out var fn)) control.FileName = fn;
            if (headers.TryGetValue("URL", out var u)) control.Url = u;
            if (headers.TryGetValue("SHA-1", out var sha)) control.Sha1 = sha;
            if (headers.TryGetValue("MTime", out var mt)) control.MTime = mt;

            if (headers.TryGetValue("Blocksize", out var bsStr) && int.TryParse(bsStr, out int bs))
                control.BlockSize = bs;

            if (headers.TryGetValue("Length", out var lenStr) && long.TryParse(lenStr, out long len))
                control.FileLength = len;

            if (headers.TryGetValue("Hash-Lengths", out var hl))
            {
                var parts = hl.Split(',');
                if (parts.Length >= 3)
                {
                    if (int.TryParse(parts[0].Trim(), out int seq)) control.SeqMatches = seq;
                    if (int.TryParse(parts[1].Trim(), out int rb)) control.RollHashBytes = rb;
                    if (int.TryParse(parts[2].Trim(), out int sb)) control.StrongHashBytes = sb;
                }
            }

            int blockCount = (int)((control.FileLength + control.BlockSize - 1) / control.BlockSize);
            control.Blocks = new List<BlockChecksum>(blockCount);

            // 2. Read binary checksum block
            int recordSize = control.RollHashBytes + control.StrongHashBytes;
            byte[] buf = new byte[recordSize];
            long offset = 0;

            for (int i = 0; i < blockCount; i++)
            {
                int totalRead = 0;
                while (totalRead < recordSize)
                {
                    int r = await stream.ReadAsync(buf.AsMemory(totalRead, recordSize - totalRead), cancellationToken).ConfigureAwait(false);
                    if (r == 0) throw new EndOfStreamException($"Unexpected end of stream reading block {i}.");
                    totalRead += r;
                }

                ulong weakHash = 0;
                for (int j = 0; j < control.RollHashBytes; j++)
                    weakHash = (weakHash << 8) | buf[j];

                ulong strongHash = 0;
                for (int j = 0; j < control.StrongHashBytes; j++)
                    strongHash |= ((ulong)buf[control.RollHashBytes + j]) << (j * 8);

                byte[] strongBytes = new byte[control.StrongHashBytes];
                Array.Copy(buf, control.RollHashBytes, strongBytes, 0, control.StrongHashBytes);

                int uncompressedLen = (int)Math.Min((long)control.BlockSize, control.FileLength - offset);

                control.Blocks.Add(new BlockChecksum
                {
                    BlockIndex = i,
                    UncompressedOffset = offset,
                    UncompressedLength = uncompressedLen,
                    WeakHash = weakHash,
                    StrongHash = strongHash,
                    StrongHashBytes = strongBytes,
                    IsCompressed = false,
                    CompressedOffset = offset,
                    CompressedLength = uncompressedLen
                });

                offset += uncompressedLen;
            }

            return control;
        }

        public async Task SaveAsync(Stream stream, CancellationToken cancellationToken = default)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"zsync: {Version}");
            if (!string.IsNullOrEmpty(FileName)) sb.AppendLine($"Filename: {FileName}");
            if (!string.IsNullOrEmpty(MTime)) sb.AppendLine($"MTime: {MTime}");
            sb.AppendLine($"Blocksize: {BlockSize}");
            sb.AppendLine($"Length: {FileLength}");
            sb.AppendLine($"Hash-Lengths: {SeqMatches},{RollHashBytes},{StrongHashBytes}");
            if (!string.IsNullOrEmpty(Url)) sb.AppendLine($"URL: {Url}");
            if (!string.IsNullOrEmpty(Sha1)) sb.AppendLine($"SHA-1: {Sha1}");
            sb.AppendLine(); // Blank line separating headers from binary data

            byte[] headerBytes = Encoding.UTF8.GetBytes(sb.ToString());
            await stream.WriteAsync(headerBytes, cancellationToken).ConfigureAwait(false);

            byte[] rec = new byte[RollHashBytes + StrongHashBytes];
            foreach (var b in Blocks)
            {
                // Write weak roll hash (big endian)
                for (int i = 0; i < RollHashBytes; i++)
                {
                    rec[i] = (byte)(b.WeakHash >> ((RollHashBytes - 1 - i) * 8));
                }

                // Write strong hash (truncated)
                if (b.StrongHashBytes != null && b.StrongHashBytes.Length >= StrongHashBytes)
                {
                    Array.Copy(b.StrongHashBytes, 0, rec, RollHashBytes, StrongHashBytes);
                }
                else
                {
                    for (int i = 0; i < StrongHashBytes; i++)
                    {
                        rec[RollHashBytes + i] = (byte)(b.StrongHash >> (i * 8));
                    }
                }

                await stream.WriteAsync(rec, cancellationToken).ConfigureAwait(false);
            }
        }

        private static async Task<string?> ReadAsciiLineAsync(Stream stream, CancellationToken cancellationToken)
        {
            using var ms = new MemoryStream();
            byte[] buf = new byte[1];
            while (true)
            {
                int r = await stream.ReadAsync(buf.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
                if (r == 0)
                {
                    if (ms.Length == 0) return null;
                    break;
                }
                byte b = buf[0];
                if (b == (byte)'\n') break;
                if (b != (byte)'\r') ms.WriteByte(b);
            }
            return Encoding.UTF8.GetString(ms.ToArray());
        }
    }
}
