using System;
using System.Runtime.CompilerServices;

namespace ZSync.Core.Hashing
{
    /// <summary>
    /// Fast 32-bit sliding-window rolling hash based on Adler-32.
    /// Provides O(1) update time as the window rolls forward byte by byte.
    /// </summary>
    public struct RollingHash
    {
        private const uint Modulo = 65521;
        private uint _s1;
        private uint _s2;
        private readonly int _windowSize;

        public RollingHash(int windowSize)
        {
            if (windowSize <= 0) throw new ArgumentOutOfRangeException(nameof(windowSize), "Window size must be positive.");
            _windowSize = windowSize;
            _s1 = 0;
            _s2 = 0;
        }

        public uint Value => (_s2 << 16) | (_s1 & 0xFFFF);

        public void Reset()
        {
            _s1 = 0;
            _s2 = 0;
        }

        /// <summary>
        /// Computes the initial hash for a block of length equal to windowSize.
        /// </summary>
        public uint Compute(ReadOnlySpan<byte> window)
        {
            if (window.Length != _windowSize)
            {
                throw new ArgumentException($"Window span length ({window.Length}) must match initialized window size ({_windowSize}).", nameof(window));
            }

            uint s1 = 0;
            uint s2 = 0;

            for (int i = 0; i < window.Length; i++)
            {
                byte b = window[i];
                s1 += b;
                s2 += (uint)(_windowSize - i) * b;
            }

            _s1 = s1 % Modulo;
            _s2 = s2 % Modulo;
            return Value;
        }

        /// <summary>
        /// Rolls the window forward by 1 byte: drops oldByte, appends newByte.
        /// Executes in O(1) time.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint Roll(byte oldByte, byte newByte)
        {
            // s1' = (s1 - oldByte + newByte) mod M
            long nextS1 = (long)_s1 - oldByte + newByte;
            nextS1 %= Modulo;
            if (nextS1 < 0) nextS1 += Modulo;

            // s2' = (s2 - windowSize * oldByte + s1') mod M
            long nextS2 = (long)_s2 - ((long)_windowSize * oldByte) + nextS1;
            nextS2 %= Modulo;
            if (nextS2 < 0) nextS2 += Modulo;

            _s1 = (uint)nextS1;
            _s2 = (uint)nextS2;
            return Value;
        }

        /// <summary>
        /// Static one-off calculation without maintaining rolling state.
        /// </summary>
        public static uint Calculate(ReadOnlySpan<byte> span)
        {
            uint s1 = 0;
            uint s2 = 0;
            int len = span.Length;

            for (int i = 0; i < len; i++)
            {
                byte b = span[i];
                s1 += b;
                s2 += (uint)(len - i) * b;
            }

            s1 %= Modulo;
            s2 %= Modulo;
            return (s2 << 16) | (s1 & 0xFFFF);
        }
    }
}
