using System;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace ZSync.Core.Hashing
{
    /// <summary>
    /// Pure C# implementation of 64-bit xxHash (xxHash64).
    /// Extreme throughput (>10 GB/s on modern CPUs) with zero external dependencies.
    /// Supports one-shot span hashing and incremental streaming state.
    /// </summary>
    public static class FastXxHash64
    {
        private const ulong Prime1 = 11400714785074694791UL;
        private const ulong Prime2 = 14029467366897019727UL;
        private const ulong Prime3 = 1609587929392839161UL;
        private const ulong Prime4 = 9650029242287828579UL;
        private const ulong Prime5 = 2870177450012600261UL;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong Round(ulong acc, ulong input)
        {
            acc += input * Prime2;
            acc = BitOperations.RotateLeft(acc, 31);
            acc *= Prime1;
            return acc;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong MergeRound(ulong acc, ulong val)
        {
            val = Round(0, val);
            acc ^= val;
            acc = acc * Prime1 + Prime4;
            return acc;
        }

        public static ulong Hash(ReadOnlySpan<byte> input, ulong seed = 0)
        {
            int length = input.Length;
            ulong h64;

            if (length >= 32)
            {
                ulong v1 = seed + Prime1 + Prime2;
                ulong v2 = seed + Prime2;
                ulong v3 = seed;
                ulong v4 = seed - Prime1;

                int offset = 0;
                int limit = length - 32;

                while (offset <= limit)
                {
                    v1 = Round(v1, ReadUInt64LittleEndian(input.Slice(offset, 8)));
                    offset += 8;
                    v2 = Round(v2, ReadUInt64LittleEndian(input.Slice(offset, 8)));
                    offset += 8;
                    v3 = Round(v3, ReadUInt64LittleEndian(input.Slice(offset, 8)));
                    offset += 8;
                    v4 = Round(v4, ReadUInt64LittleEndian(input.Slice(offset, 8)));
                    offset += 8;
                }

                h64 = BitOperations.RotateLeft(v1, 1) +
                      BitOperations.RotateLeft(v2, 7) +
                      BitOperations.RotateLeft(v3, 12) +
                      BitOperations.RotateLeft(v4, 18);

                h64 = MergeRound(h64, v1);
                h64 = MergeRound(h64, v2);
                h64 = MergeRound(h64, v3);
                h64 = MergeRound(h64, v4);

                input = input.Slice(offset);
            }
            else
            {
                h64 = seed + Prime5;
            }

            h64 += (ulong)length;

            while (input.Length >= 8)
            {
                ulong k1 = Round(0, ReadUInt64LittleEndian(input.Slice(0, 8)));
                h64 ^= k1;
                h64 = BitOperations.RotateLeft(h64, 27) * Prime1 + Prime4;
                input = input.Slice(8);
            }

            if (input.Length >= 4)
            {
                h64 ^= (ulong)ReadUInt32LittleEndian(input.Slice(0, 4)) * Prime1;
                h64 = BitOperations.RotateLeft(h64, 23) * Prime2 + Prime3;
                input = input.Slice(4);
            }

            while (input.Length > 0)
            {
                h64 ^= input[0] * Prime5;
                h64 = BitOperations.RotateLeft(h64, 11) * Prime1;
                input = input.Slice(1);
            }

            // Avalanche mix
            h64 ^= h64 >> 33;
            h64 *= Prime2;
            h64 ^= h64 >> 29;
            h64 *= Prime3;
            h64 ^= h64 >> 32;

            return h64;
        }

        public static async System.Threading.Tasks.Task<ulong> HashStreamAsync(Stream stream, ulong seed = 0, System.Threading.CancellationToken cancellationToken = default)
        {
            var state = new State(seed);
            var buffer = new byte[65536];
            int read;
            while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) > 0)
            {
                state.Update(buffer.AsSpan(0, read));
            }
            return state.Digest();
        }

        public struct State
        {
            private ulong _v1;
            private ulong _v2;
            private ulong _v3;
            private ulong _v4;
            private ulong _totalLength;
            private byte[]? _buffer;
            private int _bufferedSize;
            private readonly ulong _seed;

            public State(ulong seed = 0)
            {
                _seed = seed;
                _v1 = seed + Prime1 + Prime2;
                _v2 = seed + Prime2;
                _v3 = seed;
                _v4 = seed - Prime1;
                _totalLength = 0;
                _buffer = new byte[32];
                _bufferedSize = 0;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private void EnsureInitialized()
            {
                if (_buffer == null)
                {
                    _v1 = _seed + Prime1 + Prime2;
                    _v2 = _seed + Prime2;
                    _v3 = _seed;
                    _v4 = _seed - Prime1;
                    _buffer = new byte[32];
                }
            }

            public void Update(ReadOnlySpan<byte> input)
            {
                EnsureInitialized();
                _totalLength += (ulong)input.Length;

                if (_bufferedSize + input.Length < 32)
                {
                    input.CopyTo(_buffer.AsSpan(_bufferedSize));
                    _bufferedSize += input.Length;
                    return;
                }

                if (_bufferedSize > 0)
                {
                    int fill = 32 - _bufferedSize;
                    input.Slice(0, fill).CopyTo(_buffer.AsSpan(_bufferedSize));
                    _v1 = Round(_v1, ReadUInt64LittleEndian(_buffer.AsSpan(0, 8)));
                    _v2 = Round(_v2, ReadUInt64LittleEndian(_buffer.AsSpan(8, 8)));
                    _v3 = Round(_v3, ReadUInt64LittleEndian(_buffer.AsSpan(16, 8)));
                    _v4 = Round(_v4, ReadUInt64LittleEndian(_buffer.AsSpan(24, 8)));
                    input = input.Slice(fill);
                    _bufferedSize = 0;
                }

                while (input.Length >= 32)
                {
                    _v1 = Round(_v1, ReadUInt64LittleEndian(input.Slice(0, 8)));
                    _v2 = Round(_v2, ReadUInt64LittleEndian(input.Slice(8, 8)));
                    _v3 = Round(_v3, ReadUInt64LittleEndian(input.Slice(16, 8)));
                    _v4 = Round(_v4, ReadUInt64LittleEndian(input.Slice(24, 8)));
                    input = input.Slice(32);
                }

                if (input.Length > 0)
                {
                    input.CopyTo(_buffer.AsSpan(0));
                    _bufferedSize = input.Length;
                }
            }

            public ulong Digest()
            {
                EnsureInitialized();
                ulong h64;

                if (_totalLength >= 32)
                {
                    h64 = BitOperations.RotateLeft(_v1, 1) +
                          BitOperations.RotateLeft(_v2, 7) +
                          BitOperations.RotateLeft(_v3, 12) +
                          BitOperations.RotateLeft(_v4, 18);

                    h64 = MergeRound(h64, _v1);
                    h64 = MergeRound(h64, _v2);
                    h64 = MergeRound(h64, _v3);
                    h64 = MergeRound(h64, _v4);
                }
                else
                {
                    h64 = _seed + Prime5;
                }

                h64 += _totalLength;

                ReadOnlySpan<byte> remaining = _buffer.AsSpan(0, _bufferedSize);
                while (remaining.Length >= 8)
                {
                    ulong k1 = Round(0, ReadUInt64LittleEndian(remaining.Slice(0, 8)));
                    h64 ^= k1;
                    h64 = BitOperations.RotateLeft(h64, 27) * Prime1 + Prime4;
                    remaining = remaining.Slice(8);
                }

                if (remaining.Length >= 4)
                {
                    h64 ^= (ulong)ReadUInt32LittleEndian(remaining.Slice(0, 4)) * Prime1;
                    h64 = BitOperations.RotateLeft(h64, 23) * Prime2 + Prime3;
                    remaining = remaining.Slice(4);
                }

                while (remaining.Length > 0)
                {
                    h64 ^= remaining[0] * Prime5;
                    h64 = BitOperations.RotateLeft(h64, 11) * Prime1;
                    remaining = remaining.Slice(1);
                }

                // Avalanche mix
                h64 ^= h64 >> 33;
                h64 *= Prime2;
                h64 ^= h64 >> 29;
                h64 *= Prime3;
                h64 ^= h64 >> 32;

                return h64;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong ReadUInt64LittleEndian(ReadOnlySpan<byte> span)
        {
            return (ulong)span[0] |
                   ((ulong)span[1] << 8) |
                   ((ulong)span[2] << 16) |
                   ((ulong)span[3] << 24) |
                   ((ulong)span[4] << 32) |
                   ((ulong)span[5] << 40) |
                   ((ulong)span[6] << 48) |
                   ((ulong)span[7] << 56);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint ReadUInt32LittleEndian(ReadOnlySpan<byte> span)
        {
            return (uint)span[0] |
                   ((uint)span[1] << 8) |
                   ((uint)span[2] << 16) |
                   ((uint)span[3] << 24);
        }
    }
}
