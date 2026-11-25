using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using NUnit.Framework;
using ZSync.Core.Engine;
using ZSync.Core.Model;
using ZSync.Core.Transport;

namespace ZSync.Tests
{
    [TestFixture]
    public class HDiffPatchInteroperabilityTests
    {
        private string _tempDir = null!;
        private string _hsyncMakePath = null!;
        private string _hsyncDemoPath = null!;

        [SetUp]
        public void SetUp()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "hdiff_interop_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);

            // Locate native hsync_make and hsync_demo binaries built in reference/hsynz
            var dir = TestContext.CurrentContext.TestDirectory;
            while (dir != null && !File.Exists(Path.Combine(dir, "reference/hsynz/hsync_make")))
            {
                dir = Path.GetDirectoryName(dir);
            }

            var repoRoot = dir ?? Directory.GetCurrentDirectory();
            _hsyncMakePath = Path.Combine(repoRoot, "reference/hsynz/hsync_make");
            _hsyncDemoPath = Path.Combine(repoRoot, "reference/hsynz/hsync_demo");

            if (!File.Exists(_hsyncMakePath) || !File.Exists(_hsyncDemoPath))
            {
                Assert.Ignore($"HDiffPatch binaries not found at '{_hsyncMakePath}' or '{_hsyncDemoPath}'.");
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
        public async Task Interop_HDiffPatchMake_To_ZSyncNetSync_WithZstd()
        {
            // 1. Prepare new data (16 KB) and local old seed data sharing 75% of blocks
            int size = 16384;
            var newData = new byte[size];
            for (int i = 0; i < size; i++) newData[i] = (byte)((i * 31) ^ (i % 7));

            var oldData = (byte[])newData.Clone();
            // Mutate block 2 (between 4096 and 6144)
            for (int i = 4096; i < 6144; i++) oldData[i] ^= 0x55;

            var newFile = Path.Combine(_tempDir, "new_file.bin");
            var oldFile = Path.Combine(_tempDir, "old_file.bin");
            var hsyniFile = Path.Combine(_tempDir, "new_file.bin.hsyni");
            var hsynzFile = Path.Combine(_tempDir, "new_file.bin.hsynz");
            var reconstructedFile = Path.Combine(_tempDir, "reconstructed.bin");

            await File.WriteAllBytesAsync(newFile, newData);
            await File.WriteAllBytesAsync(oldFile, oldData);

            // 2. Run native hsync_make with zstd compression and md5 checksum
            RunProcess(_hsyncMakePath, $"-s-2048 -c-zstd -C-md5 -f \"{newFile}\" \"{hsyniFile}\" \"{hsynzFile}\"");

            Assert.That(File.Exists(hsyniFile), Is.True);
            Assert.That(File.Exists(hsynzFile), Is.True);

            // 3. Load with ZSync.Net
            var control = await ZSyncControlFile.LoadAsync(hsyniFile);
            Assert.That(control.Format, Is.EqualTo(ControlFileFormat.HDiffPatch));
            Assert.That(control.Blocks.Count, Is.EqualTo(8)); // 16384 / 2048 = 8 blocks

            // 4. Perform sync reconstruction using ZSync.Net
            using var rangeSource = new FileByteRangeSource(hsynzFile);
            var result = await ZSyncSyncEngine.SyncAsync(
                control: control,
                rangeSource: rangeSource,
                destinationPath: reconstructedFile,
                localSourcePath: oldFile);

            Assert.That(result.LocalReusedBytes, Is.GreaterThan(0));
            Assert.That(File.Exists(reconstructedFile), Is.True);

            // 5. Verify byte-for-byte identical output
            var reconstructedBytes = await File.ReadAllBytesAsync(reconstructedFile);
            Assert.That(reconstructedBytes, Is.EqualTo(newData));
        }

        [Test]
        public async Task Interop_HDiffPatchMake_ZSyncMode_To_ZSyncNetSync()
        {
            // 1. Prepare target file and seed file
            int size = 8192;
            var targetData = new byte[size];
            for (int i = 0; i < size; i++) targetData[i] = (byte)(i % 127);

            var seedData = (byte[])targetData.Clone();
            for (int i = 2048; i < 4096; i++) seedData[i] ^= 0xEE;

            var targetFile = Path.Combine(_tempDir, "target.bin");
            var seedFile = Path.Combine(_tempDir, "seed.bin");
            var zsyncFile = Path.Combine(_tempDir, "target.bin.zsync");
            var outFile = Path.Combine(_tempDir, "zsync_out.bin");

            await File.WriteAllBytesAsync(targetFile, targetData);
            await File.WriteAllBytesAsync(seedFile, seedData);

            // 2. Run native hsync_make in -zsync mode
            RunProcess(_hsyncMakePath, $"-zsync -f \"{targetFile}\" \"{zsyncFile}\"");

            Assert.That(File.Exists(zsyncFile), Is.True);

            // 3. Load with ZSync.Net
            var control = await ZSyncControlFile.LoadAsync(zsyncFile);
            Assert.That(control.Format, Is.EqualTo(ControlFileFormat.LegacyZSync));
            Assert.That(control.FileLength, Is.EqualTo(size));

            // 4. Sync using ZSync.Net against uncompressed target
            using var rangeSource = new FileByteRangeSource(targetFile);
            var result = await ZSyncSyncEngine.SyncAsync(
                control: control,
                rangeSource: rangeSource,
                destinationPath: outFile,
                localSourcePath: seedFile);

            var outBytes = await File.ReadAllBytesAsync(outFile);
            Assert.That(outBytes, Is.EqualTo(targetData));
        }

        [Test]
        public async Task Interop_ZSyncNetMake_To_HDiffPatchDemo_WithZstd()
        {
            // 1. Prepare data
            int size = 8192;
            var targetData = new byte[size];
            for (int i = 0; i < size; i++) targetData[i] = (byte)((i * 13) ^ 0x3C);

            var oldData = (byte[])targetData.Clone();
            for (int i = 0; i < 2048; i++) oldData[i] ^= 0x99;

            var targetFile = Path.Combine(_tempDir, "target.bin");
            var oldFile = Path.Combine(_tempDir, "old.bin");
            var patchedFile = Path.Combine(_tempDir, "patched_by_demo.bin");

            await File.WriteAllBytesAsync(targetFile, targetData);
            await File.WriteAllBytesAsync(oldFile, oldData);

            // 2. Generate .hsyni and .hsynz using ZSync.Net
            var summary = await ZSyncControlBuilder.GenerateAllAsync(
                targetFile,
                _tempDir,
                new ZSyncControlBuilderOptions
                {
                    BlockSize = 2048,
                    CompressType = "zstd",
                    ChecksumType = "md5"
                });

            Assert.That(File.Exists(summary.ControlPath), Is.True);
            Assert.That(File.Exists(summary.PayloadPath), Is.True);

            // 3. Run native hsync_demo to reconstruct target using oldFile + .hsyni + .hsynz
            RunProcess(_hsyncDemoPath, $"-f \"{oldFile}\" \"{summary.ControlPath}\" \"{summary.PayloadPath}\" \"{patchedFile}\"");

            Assert.That(File.Exists(patchedFile), Is.True);
            var patchedBytes = await File.ReadAllBytesAsync(patchedFile);
            Assert.That(patchedBytes, Is.EqualTo(targetData), "File reconstructed by native hsync_demo must match target byte-for-byte.");
        }

        [Test]
        public async Task Interop_ZSyncNetMake_To_HDiffPatchDemo_WithZlib()
        {
            int size = 8192;
            var targetData = new byte[size];
            for (int i = 0; i < size; i++) targetData[i] = (byte)((i * 17) ^ 0x5A);

            var oldData = (byte[])targetData.Clone();
            for (int i = 0; i < 2048; i++) oldData[i] ^= 0xCC;

            var targetFile = Path.Combine(_tempDir, "target_zlib.bin");
            var oldFile = Path.Combine(_tempDir, "old_zlib.bin");
            var patchedFile = Path.Combine(_tempDir, "patched_zlib_demo.bin");

            await File.WriteAllBytesAsync(targetFile, targetData);
            await File.WriteAllBytesAsync(oldFile, oldData);

            var summary = await ZSyncControlBuilder.GenerateAllAsync(
                targetFile,
                _tempDir,
                new ZSyncControlBuilderOptions
                {
                    BlockSize = 2048,
                    CompressType = "zlib",
                    ChecksumType = "md5"
                });

            RunProcess(_hsyncDemoPath, $"-f \"{oldFile}\" \"{summary.ControlPath}\" \"{summary.PayloadPath}\" \"{patchedFile}\"");

            Assert.That(File.Exists(patchedFile), Is.True);
            var patchedBytes = await File.ReadAllBytesAsync(patchedFile);
            Assert.That(patchedBytes, Is.EqualTo(targetData));
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
