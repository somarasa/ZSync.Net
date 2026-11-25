using System;
using System.IO;
using System.Threading.Tasks;
using NUnit.Framework;
using ZSync.Core.Engine;
using ZSync.Core.Model;
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
        public async Task SyncAsync_ZstdPayload_ReusesLocalBlocksAndDownloadsOnlyDifferences()
        {
            int blockSize = 1024;
            var targetData = new byte[4096];
            for (int i = 0; i < targetData.Length; i++) targetData[i] = (byte)((i % 32) * 7);

            // Local file has 3 of 4 blocks matching
            var localData = (byte[])targetData.Clone();
            for (int i = 1024; i < 2048; i++) localData[i] = (byte)(localData[i] ^ 0xAA);

            var targetFile = Path.Combine(_tempDir, "target.bin");
            var localFile = Path.Combine(_tempDir, "local.bin");
            var destinationFile = Path.Combine(_tempDir, "reconstructed.bin");

            await File.WriteAllBytesAsync(targetFile, targetData);
            await File.WriteAllBytesAsync(localFile, localData);

            // Generate .hsyni and .hsynz with zstd
            var summary = await ZSyncControlBuilder.GenerateAllAsync(
                targetFile,
                _tempDir,
                new ZSyncControlBuilderOptions
                {
                    BlockSize = blockSize,
                    CompressType = "zstd"
                });

            var control = await ZSyncControlFile.LoadAsync(summary.ControlPath);
            using var rangeSource = new FileByteRangeSource(summary.PayloadPath);

            var result = await ZSyncSyncEngine.SyncAsync(
                control: control,
                rangeSource: rangeSource,
                destinationPath: destinationFile,
                localSourcePath: localFile);

            Assert.That(result.LocalReusedBytes, Is.EqualTo(3072));
            Assert.That(result.DownloadedBytes, Is.GreaterThan(0));
            Assert.That(result.DownloadedBytes, Is.LessThan(1024), "Zstd compressed chunk should be smaller than 1024 bytes");

            var destinationBytes = await File.ReadAllBytesAsync(destinationFile);
            Assert.That(destinationBytes, Is.EqualTo(targetData));
        }

        [Test]
        public async Task SyncAsync_InPlaceUpdate_AtomicallyReplacesFileWithNoLeftovers()
        {
            int blockSize = 1024;
            var targetData = new byte[4096];
            for (int i = 0; i < targetData.Length; i++) targetData[i] = (byte)((i % 32) * 9);

            // Existing file on disk has 3 of 4 blocks matching
            var localData = (byte[])targetData.Clone();
            for (int i = 2048; i < 3072; i++) localData[i] = (byte)(localData[i] ^ 0xFF);

            var activeFile = Path.Combine(_tempDir, "Quantum.Worker.dll");
            await File.WriteAllBytesAsync(activeFile, localData);

            var remoteSourceFile = Path.Combine(_tempDir, "remote_payload.bin");
            await File.WriteAllBytesAsync(remoteSourceFile, targetData);

            var summary = await ZSyncControlBuilder.GenerateAllAsync(
                remoteSourceFile,
                _tempDir,
                new ZSyncControlBuilderOptions { BlockSize = blockSize, CompressType = "zstd" });

            var control = await ZSyncControlFile.LoadAsync(summary.ControlPath);
            using var rangeSource = new FileByteRangeSource(summary.PayloadPath);

            var result = await ZSyncSyncEngine.SyncAsync(
                control: control,
                rangeSource: rangeSource,
                destinationPath: activeFile,
                localSourcePath: null);

            Assert.That(result.LocalReusedBytes, Is.EqualTo(3072));
            Assert.That(result.DownloadedBytes, Is.GreaterThan(0));

            var activeBytes = await File.ReadAllBytesAsync(activeFile);
            Assert.That(activeBytes, Is.EqualTo(targetData));

            // Verify no leftover .zsnew files exist
            var leftovers = Directory.GetFiles(_tempDir, "*zsnew*");
            Assert.That(leftovers, Is.Empty);
        }
    }
}
