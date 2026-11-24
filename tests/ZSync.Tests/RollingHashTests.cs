using System;
using NUnit.Framework;
using ZSync.Core.Hashing;

namespace ZSync.Tests
{
    [TestFixture]
    public class RollingHashTests
    {
        [Test]
        public void RollingHash_Roll_ProducesIdenticalValueToCalculateFromScratch()
        {
            var data = new byte[1000];
            new Random(12345).NextBytes(data);

            int windowSize = 64;
            var roller = new RollingHash(windowSize);

            // Compute initial window
            roller.Compute(data.AsSpan(0, windowSize));
            var initialCalculated = RollingHash.Calculate(data.AsSpan(0, windowSize));
            Assert.That(roller.Value, Is.EqualTo(initialCalculated));

            // Roll across all remaining bytes and verify at every step
            for (int i = 0; i < data.Length - windowSize; i++)
            {
                byte oldByte = data[i];
                byte newByte = data[i + windowSize];

                uint rolled = roller.Roll(oldByte, newByte);
                uint calculated = RollingHash.Calculate(data.AsSpan(i + 1, windowSize));

                Assert.That(rolled, Is.EqualTo(calculated), $"Mismatch at position {i + 1}");
            }
        }
    }
}
