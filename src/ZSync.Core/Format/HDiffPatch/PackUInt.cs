using System;
using System.IO;

namespace ZSync.Core.Format.HDiffPatch
{
    /// <summary>
    /// Bit-packed variable-length integer encoding and decoding matching HDiffPatch's
    /// hpatch_packUIntWithTag and hpatch_unpackUIntWithTag algorithms.
    /// </summary>
    public static class PackUInt
    {
        public static void WriteUInt(Stream stream, ulong value) => WriteUIntWithTag(stream, value, 0, 0);

        public static void WriteUIntWithTag(Stream stream, ulong value, byte highTag, int tagBit)
        {
            if (tagBit < 0 || tagBit > 7)
                throw new ArgumentOutOfRangeException(nameof(tagBit), "tagBit must be between 0 and 7.");

            ulong maxValueWithTag = (1UL << (7 - tagBit)) - 1UL;
            int bCount = 0;
            while ((value >> (7 * bCount)) > maxValueWithTag)
                bCount++;

            byte firstByte = (byte)((byte)(value >> (7 * bCount)) | (highTag << (8 - tagBit)));
            if (bCount > 0)
                firstByte |= (byte)(1 << (7 - tagBit));

            stream.WriteByte(firstByte);

            while (bCount > 0)
            {
                bCount--;
                byte nextByte = (byte)(((value >> (7 * bCount)) & 0x7FUL) | ((bCount > 0 ? 1UL : 0UL) << 7));
                stream.WriteByte(nextByte);
            }
        }

        public static ulong ReadUInt(Stream stream) => ReadUIntWithTag(stream, 0, out _);

        public static ulong ReadUIntWithTag(Stream stream, int tagBit, out byte highTag)
        {
            if (tagBit < 0 || tagBit > 7)
                throw new ArgumentOutOfRangeException(nameof(tagBit), "tagBit must be between 0 and 7.");

            int b = stream.ReadByte();
            if (b < 0)
                throw new EndOfStreamException("Unexpected end of stream while unpacking integer.");

            byte code = (byte)b;
            highTag = tagBit > 0 ? (byte)(code >> (8 - tagBit)) : (byte)0;
            ulong value = (ulong)(code & ((1 << (7 - tagBit)) - 1));

            if ((code & (1 << (7 - tagBit))) != 0)
            {
                do
                {
                    b = stream.ReadByte();
                    if (b < 0)
                        throw new EndOfStreamException("Unexpected end of stream in continuation bytes.");
                    code = (byte)b;
                    value = (value << 7) | (ulong)(uint)(code & 0x7F);
                } while ((code & 0x80) != 0);
            }

            return value;
        }

        public static int GetPackedSize(ulong value, int tagBit = 0)
        {
            ulong maxValueWithTag = (1UL << (7 - tagBit)) - 1UL;
            int size = 0;
            while (value > maxValueWithTag)
            {
                size++;
                value >>= 7;
            }
            return size + 1;
        }
    }
}
