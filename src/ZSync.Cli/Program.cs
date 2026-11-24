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

                    case "sync":
                        return await HandleSyncAsync(args).ConfigureAwait(false);

                    case "--version":
                    case "-v":
                        Console.WriteLine("ZSync.Net v1.0.0 (High-performance xxHash64 + GZip Delta Sync)");
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
            bool generateAll = true;

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
                else if (a.Equals("--no-gzip", StringComparison.OrdinalIgnoreCase))
                {
                    generateAll = false;
                }
                else if (!a.StartsWith("-") && inputFile == null)
                {
                    inputFile = a;
                }
            }

            if (string.IsNullOrWhiteSpace(inputFile) || !File.Exists(inputFile))
            {
                Console.WriteLine("Error: Valid input file is required.");
                Console.WriteLine("Usage: dotnet ZSync.Cli.dll make <input-file> [--block-size 4096] [--out-dir <dir>]");
                return 1;
            }

            Console.WriteLine($"[ZSync] Processing '{inputFile}' (Block size: {blockSize} bytes)...");

            var options = new ZSyncControlBuilderOptions
            {
                BlockSize = blockSize
            };

            if (generateAll)
            {
                Console.WriteLine("[ZSync] Generating uncompressed .zsync, compressed .gz payload, and .gz.zsync at once (single-pass I/O)...");
                var summary = await ZSyncControlBuilder.GenerateAllAsync(inputFile, outDir, options).ConfigureAwait(false);

                Console.WriteLine("\n[ZSync] Generation completed successfully:");
                Console.WriteLine($"  Uncompressed Control: {summary.UncompressedControlPath}");
                Console.WriteLine($"  GZip Payload:         {summary.GzipPayloadPath} ({summary.GzipSize / 1024.0 / 1024.0:F2} MB, {summary.CompressionRatio:F1}% of original)");
                Console.WriteLine($"  GZip Control:         {summary.GzipControlPath}");
                Console.WriteLine($"  Total Blocks:         {summary.BlockCount:N0}");
                Console.WriteLine($"  SHA256:               {summary.Sha256}");
                Console.WriteLine($"  xxHash64:             0x{summary.XxHash64:X16}");
            }
            else
            {
                var controlPath = Path.Combine(outDir ?? Path.GetDirectoryName(inputFile)!, Path.GetFileName(inputFile) + ".zsync");
                var control = await ZSyncControlBuilder.BuildAsync(inputFile, controlPath, null, options).ConfigureAwait(false);

                Console.WriteLine("\n[ZSync] Generation completed successfully:");
                Console.WriteLine($"  Control File: {controlPath}");
                Console.WriteLine($"  Total Blocks: {control.Blocks.Count:N0}");
                Console.WriteLine($"  SHA256:       {control.Sha256}");
                Console.WriteLine($"  xxHash64:     0x{control.XxHash64:X16}");
            }

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
                Console.WriteLine("Error: --control <file.zsync|url> is required.");
                Console.WriteLine("Usage: dotnet ZSync.Cli.dll sync --control <app.zsync> [--source <old-app.zip>] [--target <new-app.zip>] [--payload <url|smb|path>]");
                return 1;
            }

            // Load control file
            ZSyncControlFile control;
            if (controlPathOrUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                controlPathOrUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                using var http = new System.Net.Http.HttpClient();
                var stream = await http.GetStreamAsync(controlPathOrUrl).ConfigureAwait(false);
                control = await ZSyncControlFile.LoadAsync(stream).ConfigureAwait(false);
            }
            else
            {
                control = await ZSyncControlFile.LoadAsync(controlPathOrUrl).ConfigureAwait(false);
            }

            targetDestination ??= control.FileName;
            payloadSource ??= control.PayloadUrl ?? Path.ChangeExtension(controlPathOrUrl, null);

            if (string.IsNullOrWhiteSpace(payloadSource))
            {
                Console.WriteLine("Error: Payload source could not be determined. Please specify --payload <url|path>.");
                return 1;
            }

            if (string.IsNullOrWhiteSpace(localSourceFile) && File.Exists(targetDestination))
            {
                localSourceFile = targetDestination;
                Console.WriteLine($"[ZSync] In-place patch mode enabled: updating '{targetDestination}' atomically.");
            }

            Console.WriteLine($"[ZSync] Target file: '{targetDestination}' ({control.FileLength / 1024.0 / 1024.0:F2} MB, {control.Blocks.Count:N0} blocks)");
            if (!string.IsNullOrWhiteSpace(localSourceFile))
            {
                Console.WriteLine($"[ZSync] Local source: '{localSourceFile}'");
            }
            Console.WriteLine($"[ZSync] Remote payload: '{payloadSource}' (Compressed: {control.IsGzipCompressed})");

            using var rangeSource = ByteRangeSourceFactory.Create(payloadSource);

            var lastReport = -1;
            var progress = new Progress<double>(pct =>
            {
                int current = (int)pct;
                if (current != lastReport && current % 10 == 0)
                {
                    lastReport = current;
                    Console.Write($"\r[ZSync] Progress: {current}% complete... ");
                }
            });

            var result = await ZSyncSyncEngine.SyncAsync(
                control,
                rangeSource,
                localSourceFile,
                targetDestination,
                progress).ConfigureAwait(false);

            Console.WriteLine("\n[ZSync] Sync finished successfully!");
            Console.WriteLine($"  Total Size:       {result.TotalBytes / 1024.0 / 1024.0:F2} MB");
            Console.WriteLine($"  Reused Locally:   {result.LocalReusedBytes / 1024.0 / 1024.0:F2} MB ({result.BandwidthSavingsPercentage:F1}% bandwidth saved)");
            Console.WriteLine($"  Downloaded:       {result.DownloadedBytes / 1024.0 / 1024.0:F2} MB");
            Console.WriteLine($"  Checksum Valid:   {result.ChecksumVerified}");

            return result.ChecksumVerified ? 0 : 1;
        }

        private static void PrintHelp()
        {
            Console.WriteLine("ZSync.Net — High-Performance xxHash64 & GZip Delta Synchronization CLI");
            Console.WriteLine("Supports HTTP/HTTPS Range Requests and Windows SMB / UNC File Shares\n");
            Console.WriteLine("Commands:");
            Console.WriteLine("  make <input-file>       Generates .zsync, .gz, and .gz.zsync files at once");
            Console.WriteLine("    --block-size <bytes>  Block size for chunking (default: 4096)");
            Console.WriteLine("    --out-dir <path>      Output directory for generated files");
            Console.WriteLine("    --no-gzip             Only generate uncompressed .zsync file\n");
            Console.WriteLine("  sync --control <file>   Syncs target file from remote/SMB using local cache");
            Console.WriteLine("    --source <old-file>   Existing local file to reuse matching blocks from");
            Console.WriteLine("    --target <dest-file>  Destination path to reconstruct target file");
            Console.WriteLine("    --payload <url|smb>   URL or SMB path (\\\\server\\share\\...) to payload file");
        }
    }
}
