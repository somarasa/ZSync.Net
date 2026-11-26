using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Threading.Tasks;
using NUnit.Framework;
using ZSync.Core.Engine;
using ZSync.Core.Model;
using ZSync.Core.Transport;

namespace ZSync.Tests
{
    [TestFixture]
    public class ZSyncDualGzipTests
    {
        private string _tempDir = null!;
        private string? _hsyncDemoPath;

        [SetUp]
        public void SetUp()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "dual_gzip_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);

            // Locate native hsync_demo binary if available
            string exeExt = OperatingSystem.IsWindows() ? ".exe" : "";
            var dir = TestContext.CurrentContext.TestDirectory;
            while (dir != null && !File.Exists(Path.Combine(dir, "reference/hsynz", "hsync_demo" + exeExt)))
            {
                dir = Path.GetDirectoryName(dir);
            }
            if (dir != null)
            {
                var candidate = Path.Combine(dir, "reference/hsynz", "hsync_demo" + exeExt);
                if (File.Exists(candidate))
                {
                    _hsyncDemoPath = candidate;
                }
            }
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_tempDir))
            {
                try { Directory.Delete(_tempDir, true); } catch { }
            }
        }

        [Test]
        public async Task DualGzip_SinglePass_GeneratesValidGzipAndControlFiles()
        {
            // Create a test file of 32 KB with repeatable pseudo-random data
            int size = 32768;
            var data = new byte[size];
            for (int i = 0; i < size; i++) data[i] = (byte)((i * 37) ^ (i / 4096));

            var sourceFile = Path.Combine(_tempDir, "dataset.bin");
            await File.WriteAllBytesAsync(sourceFile, data);

            var options = new ZSyncDualGzipOptions
            {
                BlockSize = 4096,
                GzBlockSize = 4096,
                ChecksumType = "xxh128",
                CompressionLevel = 6
            };

            var summary = await ZSyncControlBuilder.GenerateDualGzipAsync(sourceFile, _tempDir, options);

            Assert.That(File.Exists(summary.GzPayloadPath), Is.True);
            Assert.That(File.Exists(summary.ClientControlPath), Is.True);
            Assert.That(File.Exists(summary.InterSiteControlPath), Is.True);
            Assert.That(summary.UncompressedSize, Is.EqualTo(size));
            Assert.That(summary.GzPayloadSize, Is.GreaterThan(0));
            Assert.That(summary.ClientBlockCount, Is.EqualTo(8)); // 32768 / 4096 = 8
            Assert.That(summary.GzBlockCount, Is.GreaterThan(0));

            // Verify .gz with BCL GZipStream decompressor
            using (var gzIn = File.OpenRead(summary.GzPayloadPath))
            using (var gzStream = new GZipStream(gzIn, CompressionMode.Decompress))
            using (var decompressedMs = new MemoryStream())
            {
                await gzStream.CopyToAsync(decompressedMs);
                Assert.That(decompressedMs.ToArray(), Is.EqualTo(data));
            }

            // Verify .hsyni control file format and properties
            var clientControl = await ZSyncControlFile.LoadAsync(summary.ClientControlPath);
            Assert.That(clientControl.Format, Is.EqualTo(ControlFileFormat.HDiffPatch));
            Assert.That(clientControl.FileLength, Is.EqualTo(size));
            Assert.That(clientControl.Blocks.Count, Is.EqualTo(8));

            // Verify .gz.hsyni inter-site control file format and properties
            var interSiteControl = await ZSyncControlFile.LoadAsync(summary.InterSiteControlPath);
            Assert.That(interSiteControl.Format, Is.EqualTo(ControlFileFormat.HDiffPatch));
            Assert.That(interSiteControl.FileLength, Is.EqualTo(summary.GzPayloadSize));
            Assert.That(interSiteControl.Blocks.Count, Is.EqualTo(summary.GzBlockCount));
        }

        [Test]
        public async Task DualGzip_ClientReconstruct_UsingZSyncNetEngine_WithLocalSeed()
        {
            // Target file (24 KB)
            int size = 24576;
            var targetData = new byte[size];
            for (int i = 0; i < size; i++) targetData[i] = (byte)((i * 17) ^ (i % 251));

            // Old seed file with 66% identical content (block 1 modified)
            var seedData = (byte[])targetData.Clone();
            for (int i = 4096; i < 8192; i++) seedData[i] ^= 0xAA;

            var targetFile = Path.Combine(_tempDir, "Quantum.Worker.bin");
            var seedFile = Path.Combine(_tempDir, "Quantum.Worker.v1.bin");
            var reconstructedFile = Path.Combine(_tempDir, "Quantum.Worker.reconstructed.bin");

            await File.WriteAllBytesAsync(targetFile, targetData);
            await File.WriteAllBytesAsync(seedFile, seedData);

            // Generate dual gzip
            var summary = await ZSyncControlBuilder.GenerateDualGzipAsync(targetFile, _tempDir);

            // Client sync using ZSyncSyncEngine
            var control = await ZSyncControlFile.LoadAsync(summary.ClientControlPath);
            using var rangeSource = new FileByteRangeSource(summary.GzPayloadPath);

            var result = await ZSyncSyncEngine.SyncAsync(
                control: control,
                rangeSource: rangeSource,
                destinationPath: reconstructedFile,
                localSourcePath: seedFile);

            Assert.That(result.LocalReusedBytes, Is.GreaterThan(0));
            Assert.That(File.Exists(reconstructedFile), Is.True);

            var reconstructedBytes = await File.ReadAllBytesAsync(reconstructedFile);
            Assert.That(reconstructedBytes, Is.EqualTo(targetData));
        }

        [Test]
        public async Task DualGzip_ClientReconstruct_UsingNativeHDiffPatchDemo()
        {
            if (_hsyncDemoPath == null)
            {
                Assert.Ignore("Native hsync_demo binary not found.");
            }

            int size = 16384;
            var targetData = new byte[size];
            for (int i = 0; i < size; i++) targetData[i] = (byte)((i * 29) ^ (i % 19));

            var targetFile = Path.Combine(_tempDir, "app.bin");
            var reconstructedFile = Path.Combine(_tempDir, "app.native_reconstructed.bin");
            await File.WriteAllBytesAsync(targetFile, targetData);

            var summary = await ZSyncControlBuilder.GenerateDualGzipAsync(targetFile, _tempDir);

            // Run native hsync_demo against client control file and .gz payload
            RunProcess(_hsyncDemoPath!, $"-f \"\" \"{summary.ClientControlPath}\" \"{summary.GzPayloadPath}\" \"{reconstructedFile}\"");

            Assert.That(File.Exists(reconstructedFile), Is.True);
            var reconstructedBytes = await File.ReadAllBytesAsync(reconstructedFile);
            Assert.That(reconstructedBytes, Is.EqualTo(targetData));
        }

        [Test]
        public async Task DualGzip_InterSite_DeltaSyncPayload_UsingZSyncNetEngine()
        {
            // Generate version 1 of file and its dual gzip
            int size1 = 20480;
            var v1Data = new byte[size1];
            for (int i = 0; i < size1; i++) v1Data[i] = (byte)((i * 31) ^ (i % 53));
            var v1File = Path.Combine(_tempDir, "data.v1.bin");
            await File.WriteAllBytesAsync(v1File, v1Data);
            var v1Summary = await ZSyncControlBuilder.GenerateDualGzipAsync(v1File, _tempDir);

            // Generate version 2 of file with 80% shared content and its dual gzip
            var v2Data = (byte[])v1Data.Clone();
            for (int i = 8192; i < 12288; i++) v2Data[i] ^= 0x77;
            var v2File = Path.Combine(_tempDir, "data.v2.bin");
            await File.WriteAllBytesAsync(v2File, v2Data);
            var v2Summary = await ZSyncControlBuilder.GenerateDualGzipAsync(v2File, _tempDir);

            // Delta-sync .gz payload from v2 using v2's .gz.hsyni control against v1's .gz file as seed
            var interSiteControl = await ZSyncControlFile.LoadAsync(v2Summary.InterSiteControlPath);
            var replicatedGzPath = Path.Combine(_tempDir, "data.v2.replicated.gz");

            using var rangeSource = new FileByteRangeSource(v2Summary.GzPayloadPath);
            var result = await ZSyncSyncEngine.SyncAsync(
                control: interSiteControl,
                rangeSource: rangeSource,
                destinationPath: replicatedGzPath,
                localSourcePath: v1Summary.GzPayloadPath);

            Assert.That(File.Exists(replicatedGzPath), Is.True);
            var originalV2GzBytes = await File.ReadAllBytesAsync(v2Summary.GzPayloadPath);
            var replicatedGzBytes = await File.ReadAllBytesAsync(replicatedGzPath);
            Assert.That(replicatedGzBytes, Is.EqualTo(originalV2GzBytes));
        }

        [Test]
        public async Task DualGzip_InterSite_DeltaSyncPayload_UsingNativeHDiffPatchDemo()
        {
            if (_hsyncDemoPath == null)
            {
                Assert.Ignore("Native hsync_demo binary not found.");
            }

            int size = 16384;
            var targetData = new byte[size];
            for (int i = 0; i < size; i++) targetData[i] = (byte)((i * 47) ^ (i % 23));

            var targetFile = Path.Combine(_tempDir, "dataset_v2.bin");
            await File.WriteAllBytesAsync(targetFile, targetData);

            var summary = await ZSyncControlBuilder.GenerateDualGzipAsync(targetFile, _tempDir);

            var reconstructedGz = Path.Combine(_tempDir, "native_reconstructed.gz");

            // Run native hsync_demo to reconstruct .gz file from itself via .gz.hsyni
            RunProcess(_hsyncDemoPath!, $"-f \"\" \"{summary.InterSiteControlPath}\" \"{summary.GzPayloadPath}\" \"{reconstructedGz}\"");

            Assert.That(File.Exists(reconstructedGz), Is.True);
            var expectedGz = await File.ReadAllBytesAsync(summary.GzPayloadPath);
            var actualGz = await File.ReadAllBytesAsync(reconstructedGz);
            Assert.That(actualGz, Is.EqualTo(expectedGz));
        }

        [TestCase("xxh64", 2048)]
        [TestCase("xxh128", 4096)]
        [TestCase("sha256", 4096)]
        [TestCase("md5", 2048)]
        public async Task DualGzip_MultipleChecksumsAndBlockSizes_ReconstructsCleanly(string checksumType, int blockSize)
        {
            int size = 12288;
            var data = new byte[size];
            for (int i = 0; i < size; i++) data[i] = (byte)((i * 19) + (i % 31));

            var srcFile = Path.Combine(_tempDir, $"data_{checksumType}_{blockSize}.bin");
            var recFile = Path.Combine(_tempDir, $"rec_{checksumType}_{blockSize}.bin");
            await File.WriteAllBytesAsync(srcFile, data);

            var summary = await ZSyncControlBuilder.GenerateDualGzipAsync(srcFile, _tempDir, new ZSyncDualGzipOptions
            {
                BlockSize = blockSize,
                GzBlockSize = blockSize,
                ChecksumType = checksumType
            });

            var control = await ZSyncControlFile.LoadAsync(summary.ClientControlPath);
            using var rangeSource = new FileByteRangeSource(summary.GzPayloadPath);
            var result = await ZSyncSyncEngine.SyncAsync(control, rangeSource, recFile);

            var recBytes = await File.ReadAllBytesAsync(recFile);
            Assert.That(recBytes, Is.EqualTo(data));

            // Also test native demo if available (native hsync_demo compiles with xxh128, md5, sha256, sha512, crc32 plugins, but not xxh64)
            if (_hsyncDemoPath != null && checksumType != "xxh64")
            {
                var nativeRecFile = Path.Combine(_tempDir, $"native_rec_{checksumType}_{blockSize}.bin");
                RunProcess(_hsyncDemoPath!, $"-f \"\" \"{summary.ClientControlPath}\" \"{summary.GzPayloadPath}\" \"{nativeRecFile}\"");
                var nativeBytes = await File.ReadAllBytesAsync(nativeRecFile);
                Assert.That(nativeBytes, Is.EqualTo(data));
            }
        }

        private static void RunProcess(string binaryPath, string arguments)
        {
            var psi = new ProcessStartInfo
            {
                FileName = binaryPath,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to launch '{binaryPath}'.");
            string stdout = proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();

            if (proc.ExitCode != 0)
            {
                throw new InvalidOperationException($"Process '{binaryPath} {arguments}' exited with code {proc.ExitCode}.\nSTDOUT: {stdout}\nSTDERR: {stderr}");
            }
        }
    }
}
