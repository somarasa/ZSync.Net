using System;
using System.IO;
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
        public async Task GenerateAllAsync_GeneratesHDiffPatchFilesWithZstd()
        {
            var inputFile = Path.Combine(_tempDir, "app.bin");
            var data = new byte[10000];
            new Random(42).NextBytes(data);
            await File.WriteAllBytesAsync(inputFile, data);

            var summary = await ZSyncControlBuilder.GenerateAllAsync(
                inputFile,
                _tempDir,
                new ZSyncControlBuilderOptions
                {
                    BlockSize = 2048,
                    CompressType = "zstd"
                });

            Assert.That(File.Exists(summary.ControlPath), Is.True);
            Assert.That(File.Exists(summary.PayloadPath), Is.True);
            Assert.That(summary.UncompressedSize, Is.EqualTo(10000));
            Assert.That(summary.CompressedSize, Is.GreaterThan(0));
            Assert.That(summary.BlockCount, Is.EqualTo(5));

            var ctrl = await ZSyncControlFile.LoadAsync(summary.ControlPath);
            Assert.That(ctrl.Format, Is.EqualTo(ControlFileFormat.HDiffPatch));
            Assert.That(ctrl.Blocks.Count, Is.EqualTo(5));
            Assert.That(ctrl.FileLength, Is.EqualTo(10000));
            Assert.That(ctrl.CompressType, Is.EqualTo("zstdD"));
        }

        [Test]
        public async Task GenerateAllAsync_GeneratesHDiffPatchFilesWithZlib()
        {
            var inputFile = Path.Combine(_tempDir, "app_zlib.bin");
            var data = new byte[8192];
            new Random(123).NextBytes(data);
            await File.WriteAllBytesAsync(inputFile, data);

            var summary = await ZSyncControlBuilder.GenerateAllAsync(
                inputFile,
                _tempDir,
                new ZSyncControlBuilderOptions
                {
                    BlockSize = 4096,
                    CompressType = "zlib"
                });

            Assert.That(File.Exists(summary.ControlPath), Is.True);
            Assert.That(File.Exists(summary.PayloadPath), Is.True);

            var ctrl = await ZSyncControlFile.LoadAsync(summary.ControlPath);
            Assert.That(ctrl.Blocks.Count, Is.EqualTo(2));
            Assert.That(ctrl.FileLength, Is.EqualTo(8192));
            Assert.That(ctrl.CompressType, Is.EqualTo("zlibD"));
        }
    }
}
