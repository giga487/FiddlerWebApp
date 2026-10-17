// Fixture compressor adapted from the inverse codec in UOFiddler Ultima/Helpers/MythicDecompress.cs.
using System.Buffers.Binary;
using System.Runtime.InteropServices;
namespace Fiddler.Tests;
internal static class MythicFixtureEncoder
{
    public static byte[] Encode(byte[] source)
    {
        byte[] data=InternalCompress(source);
        var result=new byte[data.Length+4];
        BinaryPrimitives.WriteUInt32LittleEndian(result,(uint)source.Length ^ 0x8E2C9A3D);
        Span<byte> symbols=stackalloc byte[256];
        for(int i=0;i<256;i++) symbols[i]=(byte)i;
        for(int i=0;i<data.Length;i++)
        {
            int index=symbols.IndexOf(data[i]); result[i+4]=(byte)index;
            byte value=symbols[index];
            for(int j=index;j>0;j--) symbols[j]=symbols[j-1];
            symbols[0]=value;
        }
        return result;
    }
        public static byte[] InternalCompress(Span<byte> input)
        {
            Span<byte> symbolTable = stackalloc byte[256];
            Span<byte> frequency = stackalloc byte[256];
            Span<int> partialInput = stackalloc int[256 * 3];

            // counting frequencies
            for (int i = 0; i < input.Length; ++i)
            {
                partialInput[input[i]]++;
            }

            Frequency(partialInput, frequency);

            var nonZeroCount = 0;
            for (var i = 0; i < 256; i++)
            {
                if (partialInput[i] != 0)
                {
                    nonZeroCount++;
                }
            }

            byte[] output = new byte[input.Length + nonZeroCount + 1024];

            for (int i = 0, m = 0; i < nonZeroCount; ++i)
            {
                var freqIndex = frequency[i];
                partialInput[freqIndex + 256] = m + 1;
                m += partialInput[freqIndex];
                partialInput[freqIndex + 512] = m;
            }

            for (int i = 0; i < 256; ++i)
            {
                BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(i * 4, 4), partialInput[i]);
            }

            int count = input.Length - 1;

            Span<bool> added = stackalloc bool[256];
            int addedCount = 0;

            do
            {
                var val = input[count];

                ref int firstValRef = ref partialInput[val + 512];
                var outputAddress = firstValRef + 1024;

                // first add, just put it in symbolTable from the left and assign 0 idx
                if (!added[val])
                {
                    ShiftRight(symbolTable, addedCount);
                    symbolTable[0] = val;
                    added[val] = true;
                    addedCount++;
                    output[outputAddress] = 0;
                }
                // we're already have symbol in table, so getting it idx and putting it in output stream
                else if (firstValRef >= partialInput[val + 256])
                {
                    var idx = GetIdx(symbolTable, val, addedCount);
                    ShiftRight(symbolTable, idx);
                    symbolTable[0] = val;
                    output[outputAddress] = idx;
                }
                firstValRef--;

                count--;
            } while (count >= 0);

            for (int i = 0, m = 0; i < nonZeroCount; ++i)
            {
                var freqIndex = frequency[i];
                output[m + 1024] = GetIdx(symbolTable, freqIndex, nonZeroCount);
                m += partialInput[freqIndex];
            }

            return output;
        }

        static void Frequency(Span<int> input, Span<byte> output)
        {
            Span<int> tmp = stackalloc int[256];
            input.Slice(0, tmp.Length).CopyTo(tmp);

            for (var i = 0; i < 256; i++)
            {
                uint value = 0;
                byte index = 0;

                for (var j = 0; j < 256; j++)
                {
                    if (tmp[j] > value)
                    {
                        index = (byte)j;
                        value = (uint)tmp[j];
                    }
                }

                if (value == 0)
                {
                    break;
                }

                output[i] = index;
                tmp[index] = 0;
            }
        }

        private static byte GetIdx(Span<byte> input, byte val, int nonZeroCount)
        {
            for (byte i = 0; i < input.Length && i < nonZeroCount; ++i)
            {
                if (input[i] == val)
                {
                    return i;
                }
            }

            return 0;
        }

        static void ShiftRight(Span<byte> input, int element)
        {
            for (var i = element; i >= 1; --i)
            {
                input[i] = input[i - 1];
            }
        }
}
