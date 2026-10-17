// Adapted from UOFiddler Ultima/Helpers/UopUtils.cs (Bob Jenkins lookup3).
namespace Fiddler.Formats.Containers.Uop;

public static class UopNameHash
{
    private static uint Rotl(uint x,int k) => (x << k) | (x >> (32-k));
        public static ulong HashFileName(string input)
        {
            uint a, b, c;
            a = b = c = (uint)input.Length + 0xDEADBEEF;

            int len = input.Length, i = 0;

            // consume full 12-byte blocks
            while (len > 12)
            {
                a += (uint)(input[i]     | input[i + 1] << 8  | input[i + 2]  << 16 | input[i + 3]  << 24);
                b += (uint)(input[i + 4] | input[i + 5] << 8  | input[i + 6]  << 16 | input[i + 7]  << 24);
                c += (uint)(input[i + 8] | input[i + 9] << 8  | input[i + 10] << 16 | input[i + 11] << 24);

                // mix(a, b, c)
                a -= c; a ^= Rotl(c, 4);  c += b;
                b -= a; b ^= Rotl(a, 6);  a += c;
                c -= b; c ^= Rotl(b, 8);  b += a;
                a -= c; a ^= Rotl(c, 16); c += b;
                b -= a; b ^= Rotl(a, 19); a += c;
                c -= b; c ^= Rotl(b, 4);  b += a;

                i += 12;
                len -= 12;
            }

            // handle the trailing 1..12 bytes (intentional fall-through)
            switch (len)
            {
                case 12: c += (uint)input[i + 11] << 24; goto case 11;
                case 11: c += (uint)input[i + 10] << 16; goto case 10;
                case 10: c += (uint)input[i + 9]  << 8;  goto case 9;
                case 9:  c += (uint)input[i + 8];        goto case 8;
                case 8:  b += (uint)input[i + 7]  << 24; goto case 7;
                case 7:  b += (uint)input[i + 6]  << 16; goto case 6;
                case 6:  b += (uint)input[i + 5]  << 8;  goto case 5;
                case 5:  b += (uint)input[i + 4];        goto case 4;
                case 4:  a += (uint)input[i + 3]  << 24; goto case 3;
                case 3:  a += (uint)input[i + 2]  << 16; goto case 2;
                case 2:  a += (uint)input[i + 1]  << 8;  goto case 1;
                case 1:  a += (uint)input[i];            break;
                case 0:  return (ulong)c << 32; // empty input: no mixing, low word is 0
            }

            // final(a, b, c)
            c ^= b; c -= Rotl(b, 14);
            a ^= c; a -= Rotl(c, 11);
            b ^= a; b -= Rotl(a, 25);
            c ^= b; c -= Rotl(b, 16);
            a ^= c; a -= Rotl(c, 4);
            b ^= a; b -= Rotl(a, 14);
            c ^= b; c -= Rotl(b, 24);

            return ((ulong)b << 32) | c;
        }
}
