using System;
using System.IO;
using System.Threading.Tasks;
using ZSync.Core.Engine;
using ZSync.Core.Model;
using ZSync.Core.Transport;

namespace ZSync.Cli
{
    internal static class Program
    {
        private static async Task<int> Main(string[] args)
        {
            if (args.Length == 0)
            {
                PrintHelp();
                return 0;
            }

            var command = args[0].ToLowerInvariant();
            try
            {
                switch (command)
                {
                    case "make":
                    case "create":
                        return await HandleMakeAsync(args).ConfigureAwait(false);

                    case "make-dual":
                    case "dual":
                        return await HandleMakeDualAsync(args).ConfigureAwait(false);

                    case "sync":
                        return await HandleSyncAsync(args).ConfigureAwait(false);

                    case "--version":
                    case "-v":
                        Console.WriteLine("ZSync.Net v1.0.0 (HDiffPatch & ZSync interoperable delta sync with Zstandard / xxHash64)");
                        return 0;

                    default:
                        Console.WriteLine($"Unknown command: {command}");
                        PrintHelp();
                        return 1;
                }
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Error: {ex.Message}");
                Console.ResetColor();
                return 1;
            }
        }

        private static async Task<int> HandleMakeAsync(string[] args)
        {
            string? inputFile = null;
            string? outDir = null;
            int blockSize = 4096;
            string compressType = "zstd";
            string checksumType = "xxh64";

            for (int i = 1; i < args.Length; i++)
            {
                var a = args[i];
                if ((a.Equals("--input", StringComparison.OrdinalIgnoreCase) || a.Equals("-i", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
                {
                    inputFile = args[++i];
                }
                else if ((a.Equals("--out-dir", StringComparison.OrdinalIgnoreCase) || a.Equals("-o", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
                {
                    outDir = args[++i];
                }
                else if (a.Equals("--block-size", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    int.TryParse(args[++i], out blockSize);
                }
                else if (a.Equals("--compress", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    compressType = args[++i];
                }
                else if (a.Equals("--checksum", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    checksumType = args[++i];
                }
                else if (!a.StartsWith("-") && inputFile == null)
                {
                    inputFile = a;
                }
            }

            if (string.IsNullOrWhiteSpace(inputFile) || !File.Exists(inputFile))
            {
                Console.WriteLine("Error: Valid input file is required.");
                Console.WriteLine("Usage: zsync make <input-file> [--block-size 4096] [--compress zstd|zlib] [--out-dir <dir>]");
                return 1;
            }

            Console.WriteLine($"[ZSync] Processing '{inputFile}' (Block size: {blockSize} bytes, Compress: {compressType})...");

            var options = new ZSyncControlBuilderOptions
            {
                BlockSize = blockSize,
                CompressType = compressType,
                ChecksumType = checksumType
            };

            var summary = await ZSyncControlBuilder.GenerateAllAsync(inputFile, outDir, options).ConfigureAwait(false);

            Console.WriteLine("\n[ZSync] Generation completed successfully:");
            Console.WriteLine($"  Control File (.hsyni): {summary.ControlPath}");
            Console.WriteLine($"  Payload File (.hsynz): {summary.PayloadPath} ({summary.CompressedSize / 1024.0 / 1024.0:F2} MB, {summary.CompressionRatio:F1}% of original)");
            Console.WriteLine($"  Total Blocks:          {summary.BlockCount:N0}");
            Console.WriteLine($"  xxHash64:              0x{summary.XxHash64:X16}");

            return 0;
        }

        private static async Task<int> HandleSyncAsync(string[] args)
        {
            string? controlPathOrUrl = null;
            string? localSourceFile = null;
            string? targetDestination = null;
            string? payloadSource = null;

            for (int i = 1; i < args.Length; i++)
            {
                var a = args[i];
                if ((a.Equals("--control", StringComparison.OrdinalIgnoreCase) || a.Equals("-c", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
                {
                    controlPathOrUrl = args[++i];
                }
                else if ((a.Equals("--source", StringComparison.OrdinalIgnoreCase) || a.Equals("-s", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
                {
                    localSourceFile = args[++i];
                }
                else if ((a.Equals("--target", StringComparison.OrdinalIgnoreCase) || a.Equals("-t", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
                {
                    targetDestination = args[++i];
                }
                else if ((a.Equals("--payload", StringComparison.OrdinalIgnoreCase) || a.Equals("-p", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
                {
                    payloadSource = args[++i];
                }
            }

            if (string.IsNullOrWhiteSpace(controlPathOrUrl))
            {
                Console.WriteLine("Error: --control <file.hsyni|file.zsync|url> is required.");
                Console.WriteLine("Usage: zsync sync --control <file.hsyni> [--source <old-file>] [--target <new-file>] [--payload <url|path>]");
                return 1;
            }

            Console.WriteLine($"[ZSync] Loading control file from '{controlPathOrUrl}'...");

            ZSyncControlFile control;
            if (controlPathOrUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                controlPathOrUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                using var httpSource = new HttpByteRangeSource(controlPathOrUrl);
                using var ms = new MemoryStream();
                using var client = new System.Net.Http.HttpClient();
                using var stream = await client.GetStreamAsync(controlPathOrUrl).ConfigureAwait(false);
                await stream.CopyToAsync(ms).ConfigureAwait(false);
                ms.Position = 0;
                control = await ZSyncControlFile.LoadAsync(ms).ConfigureAwait(false);
            }
            else
            {
                control = await ZSyncControlFile.LoadAsync(controlPathOrUrl).ConfigureAwait(false);
            }

            targetDestination ??= Path.Combine(Directory.GetCurrentDirectory(), control.FileName);
            payloadSource ??= control.PayloadUrl ?? (Path.ChangeExtension(controlPathOrUrl, ".hsynz"));

            Console.WriteLine($"[ZSync] Target file:   {targetDestination}");
            Console.WriteLine($"[ZSync] Total size:    {control.FileLength / 1024.0 / 1024.0:F2} MB ({control.Blocks.Count:N0} blocks)");
            Console.WriteLine($"[ZSync] Payload from:  {payloadSource}");
            if (!string.IsNullOrEmpty(localSourceFile))
            {
                Console.WriteLine($"[ZSync] Local seed:    {localSourceFile}");
            }

            using var rangeSource = ByteRangeSourceFactory.Create(payloadSource);

            var progress = new Progress<ZSyncProgressReport>(report =>
            {
                Console.Write($"\r[ZSync] Progress: {report.Percentage:F1}% ({report.ProcessedBlocks:N0}/{report.TotalBlocks:N0} blocks) | Reused: {report.LocalReusedBytes / 1024.0 / 1024.0:F2} MB | Downloaded: {report.DownloadedBytes / 1024.0 / 1024.0:F2} MB");
            });

            Console.WriteLine("\n[ZSync] Starting synchronization...");
            var result = await ZSyncSyncEngine.SyncAsync(
                control,
                rangeSource,
                targetDestination,
                localSourceFile,
                progress).ConfigureAwait(false);

            Console.WriteLine("\n\n[ZSync] Sync finished successfully!");
            Console.WriteLine($"  Reconstructed File:  {result.TargetPath}");
            Console.WriteLine($"  Total Size:          {result.TotalBytes / 1024.0 / 1024.0:F2} MB");
            Console.WriteLine($"  Locally Reused:      {result.LocalReusedBytes / 1024.0 / 1024.0:F2} MB ({result.BandwidthSavedRatio:F1}% bandwidth saved)");
            Console.WriteLine($"  Network Downloaded:  {result.DownloadedBytes / 1024.0 / 1024.0:F2} MB");
            Console.WriteLine($"  SHA256:              {result.Sha256}");
            Console.WriteLine($"  xxHash64:            0x{result.XxHash64:X16}");

            return 0;
        }

        private static async Task<int> HandleMakeDualAsync(string[] args)
        {
            string? inputFile = null;
            string? outDir = null;
            int blockSize = 4096;
            int gzBlockSize = 4096;
            string checksumType = "xxh128";
            int level = 6;

            for (int i = 1; i < args.Length; i++)
            {
                var a = args[i];
                if ((a.Equals("--input", StringComparison.OrdinalIgnoreCase) || a.Equals("-i", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
                {
                    inputFile = args[++i];
                }
                else if ((a.Equals("--out-dir", StringComparison.OrdinalIgnoreCase) || a.Equals("-o", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
                {
                    outDir = args[++i];
                }
                else if (a.Equals("--block-size", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    int.TryParse(args[++i], out blockSize);
                }
                else if (a.Equals("--gz-block-size", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    int.TryParse(args[++i], out gzBlockSize);
                }
                else if (a.Equals("--checksum", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    checksumType = args[++i];
                }
                else if ((a.Equals("--level", StringComparison.OrdinalIgnoreCase) || a.Equals("-l", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
                {
                    int.TryParse(args[++i], out level);
                }
                else if (!a.StartsWith("-") && inputFile == null)
                {
                    inputFile = a;
                }
            }

            if (string.IsNullOrWhiteSpace(inputFile) || !File.Exists(inputFile))
            {
                Console.WriteLine("Error: Valid input file is required.");
                Console.WriteLine("Usage: zsync make-dual <input-file> [--out-dir <dir>] [--block-size 4096] [--gz-block-size 4096] [--checksum xxh128]");
                return 1;
            }

            Console.WriteLine($"[ZSync] Processing '{inputFile}' in single streaming pass...");
            Console.WriteLine($"  Client Block Size:      {blockSize} bytes");
            Console.WriteLine($"  Inter-Site Block Size:  {gzBlockSize} bytes");
            Console.WriteLine($"  Checksum Algorithm:     {checksumType}");
            Console.WriteLine($"  Compression Level:      {level}");

            var options = new ZSyncDualGzipOptions
            {
                BlockSize = blockSize,
                GzBlockSize = gzBlockSize,
                ChecksumType = checksumType,
                CompressionLevel = level
            };

            var summary = await ZSyncControlBuilder.GenerateDualGzipAsync(inputFile, outDir, options).ConfigureAwait(false);

            Console.WriteLine("\n[ZSync] Single-pass dual-control generation completed successfully:");
            Console.WriteLine($"  GZip Payload:           {summary.GzPayloadPath} ({summary.GzPayloadSize / 1024.0 / 1024.0:F2} MB, {summary.CompressionRatio:F1}% of original)");
            Console.WriteLine($"  Client Control (.hsyni):{summary.ClientControlPath} ({summary.ClientBlockCount:N0} blocks)");
            Console.WriteLine($"  Inter-Site Control:     {summary.InterSiteControlPath} ({summary.GzBlockCount:N0} blocks)");
            Console.WriteLine($"  Source xxHash64:        0x{summary.UncompressedXxHash64:X16}");
            Console.WriteLine($"  Payload xxHash64:       0x{summary.GzXxHash64:X16}");
            Console.WriteLine($"  GZip CRC-32:            0x{summary.Crc32:X8}");

            return 0;
        }

        private static void PrintHelp()
        {
            Console.WriteLine("ZSync.Net — High-Performance HDiffPatch & ZSync Delta Synchronization Tool\n");
            Console.WriteLine("Usage:");
            Console.WriteLine("  zsync make <file> [options]");
            Console.WriteLine("      --block-size <bytes>      Block size in bytes (default: 4096)");
            Console.WriteLine("      --compress <type>         Compression type: zstd, zlib (default: zstd)");
            Console.WriteLine("      --out-dir <directory>     Output destination directory");
            Console.WriteLine();
            Console.WriteLine("  zsync make-dual <file> [options]");
            Console.WriteLine("      --block-size <bytes>      Client reconstruction block size (default: 4096)");
            Console.WriteLine("      --gz-block-size <bytes>   Inter-site replication block size (default: 4096)");
            Console.WriteLine("      --checksum <type>         Checksum type: xxh128, xxh64, md5, sha256 (default: xxh128)");
            Console.WriteLine("      --level <1-9>             Gzip compression level (default: 6)");
            Console.WriteLine("      --out-dir <directory>     Output destination directory");
            Console.WriteLine();
            Console.WriteLine("  zsync sync --control <file.hsyni|file.zsync|url> [options]");
            Console.WriteLine("      --target <file>           Target destination output file");
            Console.WriteLine("      --source <file>           Local seed file to scan for identical chunks");
            Console.WriteLine("      --payload <url|path>      Source payload (.hsynz/.gz) location (HTTP Range or SMB/File)");
        }
    }
}
