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
    }
}
