using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace ZSync.Core.Hashing
{
    /// <summary>
    /// Fast pure managed implementation of MD4 (RFC 1320) for backward compatibility
    /// with legacy Colin Phipps zsync control files (.zsync).
    /// </summary>
    public static class Md4
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint F(uint x, uint y, uint z) => (x & y) | (~x & z);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint G(uint x, uint y, uint z) => (x & y) | (x & z) | (y & z);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint H(uint x, uint y, uint z) => x ^ y ^ z;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint RotateLeft(uint x, int n) => (x << n) | (x >> (32 - n));

        public static byte[] Hash(ReadOnlySpan<byte> input)
        {
            byte[] output = new byte[16];
            Hash(input, output);
            return output;
        }

        public static void Hash(ReadOnlySpan<byte> input, Span<byte> output)
        {
            if (output.Length < 16)
                throw new ArgumentException("Output buffer must be at least 16 bytes.", nameof(output));

            uint a = 0x67452301;
            uint b = 0xefcdab89;
            uint c = 0x98badcfe;
            uint d = 0x10325476;

            ulong bitLength = (ulong)input.Length * 8UL;
            int paddedLength = (input.Length + 8 + 64) & ~63;
            byte[] padded = new byte[paddedLength];
            input.CopyTo(padded);
            padded[input.Length] = 0x80;
            BinaryPrimitives.WriteUInt64LittleEndian(padded.AsSpan(paddedLength - 8), bitLength);

            Span<uint> x = stackalloc uint[16];

            for (int offset = 0; offset < paddedLength; offset += 64)
            {
                var block = padded.AsSpan(offset, 64);
                for (int i = 0; i < 16; i++)
                    x[i] = BinaryPrimitives.ReadUInt32LittleEndian(block.Slice(i * 4, 4));

                uint aa = a, bb = b, cc = c, dd = d;

                // Round 1
                a = RotateLeft(a + F(b, c, d) + x[0], 3);
                d = RotateLeft(d + F(a, b, c) + x[1], 7);
                c = RotateLeft(c + F(d, a, b) + x[2], 11);
                b = RotateLeft(b + F(c, d, a) + x[3], 19);
                a = RotateLeft(a + F(b, c, d) + x[4], 3);
                d = RotateLeft(d + F(a, b, c) + x[5], 7);
                c = RotateLeft(c + F(d, a, b) + x[6], 11);
                b = RotateLeft(b + F(c, d, a) + x[7], 19);
                a = RotateLeft(a + F(b, c, d) + x[8], 3);
                d = RotateLeft(d + F(a, b, c) + x[9], 7);
                c = RotateLeft(c + F(d, a, b) + x[10], 11);
                b = RotateLeft(b + F(c, d, a) + x[11], 19);
                a = RotateLeft(a + F(b, c, d) + x[12], 3);
                d = RotateLeft(d + F(a, b, c) + x[13], 7);
                c = RotateLeft(c + F(d, a, b) + x[14], 11);
                b = RotateLeft(b + F(c, d, a) + x[15], 19);

                // Round 2
                a = RotateLeft(a + G(b, c, d) + x[0] + 0x5a827999, 3);
                d = RotateLeft(d + G(a, b, c) + x[4] + 0x5a827999, 5);
                c = RotateLeft(c + G(d, a, b) + x[8] + 0x5a827999, 9);
                b = RotateLeft(b + G(c, d, a) + x[12] + 0x5a827999, 13);
                a = RotateLeft(a + G(b, c, d) + x[1] + 0x5a827999, 3);
                d = RotateLeft(d + G(a, b, c) + x[5] + 0x5a827999, 5);
                c = RotateLeft(c + G(d, a, b) + x[9] + 0x5a827999, 9);
                b = RotateLeft(b + G(c, d, a) + x[13] + 0x5a827999, 13);
                a = RotateLeft(a + G(b, c, d) + x[2] + 0x5a827999, 3);
                d = RotateLeft(d + G(a, b, c) + x[6] + 0x5a827999, 5);
                c = RotateLeft(c + G(d, a, b) + x[10] + 0x5a827999, 9);
                b = RotateLeft(b + G(c, d, a) + x[14] + 0x5a827999, 13);
                a = RotateLeft(a + G(b, c, d) + x[3] + 0x5a827999, 3);
                d = RotateLeft(d + G(a, b, c) + x[7] + 0x5a827999, 5);
                c = RotateLeft(c + G(d, a, b) + x[11] + 0x5a827999, 9);
                b = RotateLeft(b + G(c, d, a) + x[15] + 0x5a827999, 13);

                // Round 3
                a = RotateLeft(a + H(b, c, d) + x[0] + 0x6ed9eba1, 3);
                d = RotateLeft(d + H(a, b, c) + x[8] + 0x6ed9eba1, 9);
                c = RotateLeft(c + H(d, a, b) + x[4] + 0x6ed9eba1, 11);
                b = RotateLeft(b + H(c, d, a) + x[12] + 0x6ed9eba1, 15);
                a = RotateLeft(a + H(b, c, d) + x[2] + 0x6ed9eba1, 3);
                d = RotateLeft(d + H(a, b, c) + x[10] + 0x6ed9eba1, 9);
                c = RotateLeft(c + H(d, a, b) + x[6] + 0x6ed9eba1, 11);
                b = RotateLeft(b + H(c, d, a) + x[14] + 0x6ed9eba1, 15);
                a = RotateLeft(a + H(b, c, d) + x[1] + 0x6ed9eba1, 3);
                d = RotateLeft(d + H(a, b, c) + x[9] + 0x6ed9eba1, 9);
                c = RotateLeft(c + H(d, a, b) + x[5] + 0x6ed9eba1, 11);
                b = RotateLeft(b + H(c, d, a) + x[13] + 0x6ed9eba1, 15);
                a = RotateLeft(a + H(b, c, d) + x[3] + 0x6ed9eba1, 3);
                d = RotateLeft(d + H(a, b, c) + x[11] + 0x6ed9eba1, 9);
                c = RotateLeft(c + H(d, a, b) + x[7] + 0x6ed9eba1, 11);
                b = RotateLeft(b + H(c, d, a) + x[15] + 0x6ed9eba1, 15);

                a += aa;
                b += bb;
                c += cc;
                d += dd;
            }

            BinaryPrimitives.WriteUInt32LittleEndian(output.Slice(0, 4), a);
            BinaryPrimitives.WriteUInt32LittleEndian(output.Slice(4, 4), b);
            BinaryPrimitives.WriteUInt32LittleEndian(output.Slice(8, 4), c);
            BinaryPrimitives.WriteUInt32LittleEndian(output.Slice(12, 4), d);
        }
    }
}
