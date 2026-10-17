namespace Fiddler.Formats.Gumps;

public sealed record GumpRecord(byte[] Data, int Width, int Height)
{
    public DecodedImage Decode() => GumpDecoder.Decode(Data, Width, Height);
}

public interface IGumpArchive : IDisposable
{
    IReadOnlyList<MulEntry> Entries { get; }
    int SlotCount { get; }
    GumpRecord ReadRecord(int id);
    DecodedImage Read(int id);
}
