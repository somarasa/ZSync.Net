using System;
using System.IO;
using System.Threading.Tasks;
using NUnit.Framework;
using ZSync.Core.Engine;
using ZSync.Core.Transport;

namespace ZSync.Tests
{
    [TestFixture]
    public class ZSyncSyncEngineTests
    {
        private string _tempDir = null!;

        [SetUp]
        public void SetUp()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "sync_engine_tests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
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
        public async Task SyncAsync_UncompressedPayload_ReusesLocalBlocksAndDownloadsOnlyDifferences()
        {
            int blockSize = 1024;
            // Target file: 4 blocks (4096 bytes)
            var targetData = new byte[4096];
            new Random(100).NextBytes(targetData);

            // Local base file: blocks 0, 1, and 3 match target, but block 2 is different!
            var localData = (byte[])targetData.Clone();
            for (int i = 2048; i < 3072; i++) localData[i] = (byte)(localData[i] ^ 0xFF);

            var targetFile = Path.Combine(_tempDir, "target.bin");
            var localFile = Path.Combine(_tempDir, "local.bin");
            var controlFile = Path.Combine(_tempDir, "target.bin.zsync");
            var destinationFile = Path.Combine(_tempDir, "reconstructed.bin");

            await File.WriteAllBytesAsync(targetFile, targetData);
            await File.WriteAllBytesAsync(localFile, localData);

            // Generate control file
            var control = await ZSyncControlBuilder.BuildAsync(
                targetFile,
                controlFile,
                null,
                new ZSyncControlBuilderOptions { BlockSize = blockSize });

            // Run Sync
            using var rangeSource = new FileByteRangeSource(targetFile);
            var result = await ZSyncSyncEngine.SyncAsync(
                control,
                rangeSource,
                localFile,
                destinationFile);

            Assert.That(result.ChecksumVerified, Is.True, "Checksum of reconstructed file must match target.");
            Assert.That(result.TotalBytes, Is.EqualTo(4096));
            Assert.That(result.LocalReusedBytes, Is.EqualTo(3072)); // 3 blocks reused locally
            Assert.That(result.DownloadedBytes, Is.EqualTo(1024));  // Only 1 missing block downloaded
            Assert.That(result.BandwidthSavingsPercentage, Is.EqualTo(75.0).Within(0.01));

            var destinationBytes = await File.ReadAllBytesAsync(destinationFile);
            Assert.That(destinationBytes, Is.EqualTo(targetData));
        }

        [Test]
        public async Task SyncAsync_GzipPayload_DownloadsCompressedDifferenceAndReconstructsExactFile()
        {
            int blockSize = 1024;
            // Create compressible binary data (repeating pattern simulating binary service)
            var targetData = new byte[4096];
            for (int i = 0; i < targetData.Length; i++) targetData[i] = (byte)((i % 32) * 7);

            // Local file has 3 of 4 blocks matching
            var localData = (byte[])targetData.Clone();
            for (int i = 1024; i < 2048; i++) localData[i] = (byte)(localData[i] ^ 0xAA);

            var targetFile = Path.Combine(_tempDir, "target.bin");
            var localFile = Path.Combine(_tempDir, "local.bin");
            var destinationFile = Path.Combine(_tempDir, "reconstructed_gzip.bin");

            await File.WriteAllBytesAsync(targetFile, targetData);
            await File.WriteAllBytesAsync(localFile, localData);

            // Generate uncompressed + gz payload + gz.zsync at once
            var summary = await ZSyncControlBuilder.GenerateAllAsync(
                targetFile,
                _tempDir,
                new ZSyncControlBuilderOptions { BlockSize = blockSize });

            // Load gzip control
            var gzipControl = await ZSync.Core.Model.ZSyncControlFile.LoadAsync(summary.GzipControlPath);

            // Range source points to the .gz file!
            using var rangeSource = new FileByteRangeSource(summary.GzipPayloadPath);

            var result = await ZSyncSyncEngine.SyncAsync(
                gzipControl,
                rangeSource,
                localFile,
                destinationFile);

            Assert.That(result.ChecksumVerified, Is.True, "Reconstructed file from GZip delta must pass checksum.");
            Assert.That(result.LocalReusedBytes, Is.EqualTo(3072)); // 3 blocks uncompressed from local file
            Assert.That(result.DownloadedBytes, Is.LessThan(1024), "Missing block downloaded must be GZip compressed.");

            var destinationBytes = await File.ReadAllBytesAsync(destinationFile);
            Assert.That(destinationBytes, Is.EqualTo(targetData));
        }
    }
}
