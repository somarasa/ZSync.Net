using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using ZSync.Core.Engine;
using ZSync.Core.Model;

namespace ZSync.Tests
{
    [TestFixture]
    public class ZSyncControlBuilderTests
    {
        private string _tempDir = null!;

        [SetUp]
        public void SetUp()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "builder_tests_" + Guid.NewGuid().ToString("N"));
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
        public async Task GenerateAllAsync_GeneratesUncompressedAndGzipAndControlFilesAtOnce()
        {
            var inputFile = Path.Combine(_tempDir, "app.bin");
            var data = new byte[10000];
            new Random(42).NextBytes(data);
            await File.WriteAllBytesAsync(inputFile, data);

            var summary = await ZSyncControlBuilder.GenerateAllAsync(
                inputFile,
                _tempDir,
                new ZSyncControlBuilderOptions { BlockSize = 2048 });

            Assert.That(File.Exists(summary.UncompressedControlPath), Is.True);
            Assert.That(File.Exists(summary.GzipPayloadPath), Is.True);
            Assert.That(File.Exists(summary.GzipControlPath), Is.True);

            Assert.That(summary.UncompressedSize, Is.EqualTo(10000));
            Assert.That(summary.GzipSize, Is.GreaterThan(0));
            Assert.That(summary.BlockCount, Is.EqualTo(5)); // 10000 / 2048 = 4 full blocks + 1 partial block

            // Load uncompressed control
            var uncompressedCtrl = await ZSyncControlFile.LoadAsync(summary.UncompressedControlPath);
            Assert.That(uncompressedCtrl.IsGzipCompressed, Is.False);
            Assert.That(uncompressedCtrl.Blocks.Count, Is.EqualTo(5));
            Assert.That(uncompressedCtrl.FileLength, Is.EqualTo(10000));

            // Load gzip control
            var gzipCtrl = await ZSyncControlFile.LoadAsync(summary.GzipControlPath);
            Assert.That(gzipCtrl.IsGzipCompressed, Is.True);
            Assert.That(gzipCtrl.Blocks.Count, Is.EqualTo(5));
            Assert.That(gzipCtrl.FileLength, Is.EqualTo(10000));
            Assert.That(gzipCtrl.CompressedFileLength, Is.EqualTo(summary.GzipSize));
            Assert.That(gzipCtrl.Blocks[0].IsCompressed, Is.True);
        }
    }
}
