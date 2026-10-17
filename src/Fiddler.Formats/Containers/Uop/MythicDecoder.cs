// Decoder adapted from UOFiddler Ultima/Helpers/MythicDecompress.cs.
using System.Buffers.Binary;
using System.Runtime.InteropServices;
namespace Fiddler.Formats.Containers.Uop;
internal static class MythicDecoder
{
    private const int FrequencyHeaderSize = 1024;
    public static byte[] Decode(ReadOnlySpan<byte> input, int limit)
    {
        if (input.Length < 4) throw new InvalidDataException("Header Mythic troncato.");
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(input) ^ 0x8E2C9A3D;
        if (size > limit) throw new InvalidDataException("Payload Mythic oltre il limite.");
        var decoded = new byte[input.Length-4];
        Span<byte> symbols = stackalloc byte[256];
        for (int i=0;i<256;i++) symbols[i]=(byte)i;
        for (int i=0;i<decoded.Length;i++)
        {
            int index=input[i+4]; byte value=symbols[index]; decoded[i]=value;
            for(int j=index;j>0;j--) symbols[j]=symbols[j-1];
            symbols[0]=value;
        }
        var result=new byte[(int)size];
        if (!TryInternalDecompress(decoded,result,out int written) || written!=result.Length)
            throw new InvalidDataException("Payload Mythic non valido.");
        return result;
    }
        public static bool TryInternalDecompress(ReadOnlySpan<byte> input, Span<byte> destination, out int written)
        {
            written = 0;

            try
            {
                if (input.Length < FrequencyHeaderSize)
                {
                    return false;
                }

                Span<byte> symbolTable = stackalloc byte[256];
                Span<byte> frequency = stackalloc byte[256];
                Span<int> partialInput = stackalloc int[256 * 3];

                partialInput.Clear();

                for (int i = 0; i < 256; i++)
                {
                    symbolTable[i] = (byte)i;
                }

                input.Slice(0, FrequencyHeaderSize).CopyTo(MemoryMarshal.AsBytes(partialInput));

                long total = 0;
                for (int i = 0; i < 256; i++)
                {
                    if (partialInput[i] < 0) return false;
                    total += partialInput[i];
                }

                if (total > destination.Length) return false;
                int sum = (int)total;
                if (sum == 0)
                {
                    written = 0;
                    return true;
                }

                if (destination.Length < sum)
                {
                    return false;
                }

                int nonZeroCount = 0;
                for (int i = 0; i < 256; i++)
                {
                    if (partialInput[i] != 0)
                    {
                        nonZeroCount++;
                    }
                }

                Frequency(partialInput, frequency);

                for (int i = 0, m = 0; i < nonZeroCount; ++i)
                {
                    var freq = frequency[i];
                    symbolTable[input[m + FrequencyHeaderSize]] = freq;
                    partialInput[freq + 256] = m + 1;
                    m += partialInput[freq];
                    partialInput[freq + 512] = m;
                }

                byte val = symbolTable[0];
                int count = 0;

                do
                {
                    ref int firstValRef = ref partialInput[val + 256];
                    destination[count] = val;

                    if (firstValRef < partialInput[val + 512])
                    {
                        byte idx = input[firstValRef + FrequencyHeaderSize];
                        firstValRef++;

                        if (idx != 0)
                        {
                            ShiftLeft(symbolTable, idx);

                            symbolTable[idx] = val;
                            val = symbolTable[0];
                        }
                    }
                    else if (nonZeroCount-- > 0)
                    {
                        ShiftLeft(symbolTable, nonZeroCount);

                        val = symbolTable[0];
                    }

                    count++;
                } while (count < sum);

                written = sum;
                return true;
            }
            catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or OverflowException)
            {
                return false;
            }
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

        static void ShiftLeft(Span<byte> input, int element)
        {
            for (var i = 0; i < element; ++i)
            {
                input[i] = input[i + 1];
            }
        }
}
