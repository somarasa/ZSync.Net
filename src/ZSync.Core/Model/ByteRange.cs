using System;
using System.Collections.Generic;
using System.Linq;

namespace ZSync.Core.Model
{
    public readonly struct ByteRange : IEquatable<ByteRange>
    {
        public long Offset { get; }
        public int Length { get; }
        public long EndOffset => Offset + Length - 1;

        public ByteRange(long offset, int length)
        {
            if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
            if (length <= 0) throw new ArgumentOutOfRangeException(nameof(length));
            Offset = offset;
            Length = length;
        }

        public bool Equals(ByteRange other) => Offset == other.Offset && Length == other.Length;
        public override bool Equals(object? obj) => obj is ByteRange other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(Offset, Length);
        public override string ToString() => $"bytes={Offset}-{EndOffset} ({Length} bytes)";

        /// <summary>
        /// Coalesces adjacent or overlapping byte ranges to minimize network roundtrips.
        /// </summary>
        public static List<ByteRange> Coalesce(IEnumerable<ByteRange> ranges, int maxGap = 0)
        {
            var sorted = ranges.OrderBy(r => r.Offset).ToList();
            if (sorted.Count <= 1) return sorted;

            var result = new List<ByteRange>();
            var current = sorted[0];

            for (int i = 1; i < sorted.Count; i++)
            {
                var next = sorted[i];
                if (next.Offset <= current.Offset + current.Length + maxGap)
                {
                    long newEnd = Math.Max(current.EndOffset, next.EndOffset);
                    current = new ByteRange(current.Offset, (int)(newEnd - current.Offset + 1));
                }
                else
                {
                    result.Add(current);
                    current = next;
                }
            }

            result.Add(current);
            return result;
        }
    }
}
