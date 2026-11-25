using System;
using System.Text;
using NUnit.Framework;
using ZSync.Core.Hashing;

namespace ZSync.Tests
{
    [TestFixture]
    public class FastXxHash64Tests
    {
        [Test]
        public void Block0_CheckChecksum_MatchesNative()
        {
            byte[] block = new byte[2048];
            using (var f = System.IO.File.OpenRead("/root/workspace/ZSync.Net/reference/hsynz/hsync_make"))
            {
                f.ReadExactly(block, 0, 2048);
            }
            byte[] strong = ChecksumHelper.ComputeStrongChecksum("xxh128", block);
            Console.WriteLine("C# Block 0 strong: " + Convert.ToHexString(strong).ToLowerInvariant());

            var cc = new HDiffPatchCheckChecksum("xxh128", 16);
            cc.AppendBlock(0, strong);
            byte[] res = cc.GetResult();
            Console.WriteLine("C# Check checksum: " + Convert.ToHexString(res).ToLowerInvariant());

            Assert.That(Convert.ToHexString(strong).ToLowerInvariant(), Is.EqualTo("f9b705f314218f2018f9d7329d6e7105"));
            Assert.That(Convert.ToHexString(res).ToLowerInvariant(), Is.EqualTo("6fa6ebc3c94e759f522eb759a86e18af"));
        }

        [Test]
        public void XxHash128_MatchesNative()
        {
            var bytes = Encoding.UTF8.GetBytes("The quick brown fox jumps over the lazy dog");
            var h = ChecksumHelper.ComputeStrongChecksum("xxh128", bytes);
            Console.WriteLine("C# xxh128: " + Convert.ToHexString(h).ToLowerInvariant());
            Assert.That(Convert.ToHexString(h).ToLowerInvariant(), Is.EqualTo("51768a3a2ecca124fae7a35c2050d6dd"));
        }

        [Test]
        public void Hash_EmptyInput_ProducesValidHash()
        {
            var hash = FastXxHash64.Hash(ReadOnlySpan<byte>.Empty);
            Assert.That(hash, Is.Not.EqualTo(0UL));
        }

        [Test]
        public void Hash_IdenticalInputs_ProduceIdenticalHashes()
        {
            var bytes1 = Encoding.UTF8.GetBytes("The quick brown fox jumps over the lazy dog");
            var bytes2 = Encoding.UTF8.GetBytes("The quick brown fox jumps over the lazy dog");

            var h1 = FastXxHash64.Hash(bytes1);
            var h2 = FastXxHash64.Hash(bytes2);

            Assert.That(h1, Is.EqualTo(h2));
        }

        [Test]
        public void Hash_DifferentInputs_ProduceDifferentHashes()
        {
            var bytes1 = Encoding.UTF8.GetBytes("Block 1 data for version 1.0.0");
            var bytes2 = Encoding.UTF8.GetBytes("Block 1 data for version 1.0.1");

            var h1 = FastXxHash64.Hash(bytes1);
            var h2 = FastXxHash64.Hash(bytes2);

            Assert.That(h1, Is.Not.EqualTo(h2));
        }

        [Test]
        public void Hash_LargePayload_MatchesAcrossChunkOffsets()
        {
            var data = new byte[10000];
            new Random(42).NextBytes(data);

            var fullHash = FastXxHash64.Hash(data);

            // Subspan of 4096 bytes
            var sub1 = FastXxHash64.Hash(data.AsSpan(0, 4096));
            var sub2 = FastXxHash64.Hash(data.AsSpan(0, 4096));

            Assert.That(sub1, Is.EqualTo(sub2));
            Assert.That(sub1, Is.Not.EqualTo(fullHash));
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(7)]
        [TestCase(31)]
        [TestCase(32)]
        [TestCase(33)]
        [TestCase(64)]
        [TestCase(100)]
        [TestCase(1024)]
        [TestCase(70000)]
        public void State_MatchesDirectHash_AcrossArbitraryChunkSizes(int totalSize)
        {
            var data = new byte[totalSize];
            new Random(12345).NextBytes(data);
            ulong expected = FastXxHash64.Hash(data);

            // Test various slice step sizes (e.g. 1 byte, 17 bytes, 32 bytes, 1024 bytes)
            int[] chunkSizes = { 1, 7, 16, 31, 32, 63, 1024, 65536 };
            foreach (var chunkSize in chunkSizes)
            {
                var state = new FastXxHash64.State();
                int offset = 0;
                while (offset < data.Length)
                {
                    int take = Math.Min(chunkSize, data.Length - offset);
                    state.Update(data.AsSpan(offset, take));
                    offset += take;
                }
                ulong actual = state.Digest();
                Assert.That(actual, Is.EqualTo(expected), $"Failed with chunkSize {chunkSize} on totalSize {totalSize}");
            }
        }

        [Test]
        public async System.Threading.Tasks.Task HashStreamAsync_MatchesDirectHash()
        {
            var data = new byte[128 * 1024];
            new Random(999).NextBytes(data);
            ulong expected = FastXxHash64.Hash(data);

            using var ms = new System.IO.MemoryStream(data);
            ulong actual = await FastXxHash64.HashStreamAsync(ms);

            Assert.That(actual, Is.EqualTo(expected));
        }
    }
}
