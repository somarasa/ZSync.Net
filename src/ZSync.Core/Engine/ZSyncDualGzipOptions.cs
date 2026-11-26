using System;
using ZSync.Core.Model;

namespace ZSync.Core.Engine
{
    /// <summary>
    /// Configuration options for single-pass dual-control Gzip generation.
    /// </summary>
    public sealed class ZSyncDualGzipOptions
    {
        /// <summary>
        /// Block size for client reconstruction of uncompressed source data (default: 4096 bytes).
        /// </summary>
        public int BlockSize { get; set; } = 4096;

        /// <summary>
        /// Block size for inter-site replication of the .gz payload (default: 4096 bytes).
        /// </summary>
        public int GzBlockSize { get; set; } = 4096;

        /// <summary>
        /// Checksum algorithm (default: "xxh128"). Supported: "xxh128", "xxh64", "md5", "sha256", "sha512", "crc32".
        /// </summary>
        public string ChecksumType { get; set; } = "xxh128";

        /// <summary>
        /// Deflate compression level (1 to 9). Default: 6 (Optimal).
        /// </summary>
        public int CompressionLevel { get; set; } = 6;

        /// <summary>
        /// Format of the client control file (default: HDiffPatch .hsyni).
        /// </summary>
        public ControlFileFormat ClientFormat { get; set; } = ControlFileFormat.HDiffPatch;
    }

    /// <summary>
    /// Summary of results produced by single-pass dual-control Gzip generation.
    /// </summary>
    public sealed class ZSyncDualGzipSummary
    {
        /// <summary>
        /// Path to the original uncompressed source file.
        /// </summary>
        public string SourcePath { get; set; } = string.Empty;

        /// <summary>
        /// Path to the generated RFC 1952 .gz payload file.
        /// </summary>
        public string GzPayloadPath { get; set; } = string.Empty;

        /// <summary>
        /// Path to the client control file (e.g. source.bin.hsyni).
        /// </summary>
        public string ClientControlPath { get; set; } = string.Empty;

        /// <summary>
        /// Path to the inter-site replication control file (source.bin.gz.hsyni).
        /// </summary>
        public string InterSiteControlPath { get; set; } = string.Empty;

        /// <summary>
        /// Total size of the uncompressed source file in bytes.
        /// </summary>
        public long UncompressedSize { get; set; }

        /// <summary>
        /// Total size of the generated .gz payload file in bytes.
        /// </summary>
        public long GzPayloadSize { get; set; }

        /// <summary>
        /// Number of blocks in the client control file.
        /// </summary>
        public int ClientBlockCount { get; set; }

        /// <summary>
        /// Number of blocks in the inter-site control file.
        /// </summary>
        public int GzBlockCount { get; set; }

        /// <summary>
        /// xxHash64 of the original uncompressed file.
        /// </summary>
        public ulong UncompressedXxHash64 { get; set; }

        /// <summary>
        /// xxHash64 of the generated .gz payload file.
        /// </summary>
        public ulong GzXxHash64 { get; set; }

        /// <summary>
        /// CRC-32 of the uncompressed source data as stored in the GZip trailer.
        /// </summary>
        public uint Crc32 { get; set; }

        /// <summary>
        /// Compression ratio percentage (GzPayloadSize / UncompressedSize * 100).
        /// </summary>
        public double CompressionRatio => UncompressedSize > 0 ? (double)GzPayloadSize / UncompressedSize * 100.0 : 0.0;
    }
}
