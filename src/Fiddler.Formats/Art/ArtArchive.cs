using Fiddler.Formats.Containers.Uop;

namespace Fiddler.Formats.Art;

/// <summary>On-demand art lookup for scene rendering, from MUL/IDX or ArtLegacyMUL UOP.</summary>
public sealed class ArtArchive : IDisposable
{
    private readonly ArtMulReader? mul;
    private readonly UopArchive? uop;
    public string Format => uop is null ? "Art MUL / IDX" : "Art UOP";
    private ArtArchive(ArtMulReader mul) => this.mul = mul;
    private ArtArchive(UopArchive uop) => this.uop = uop;
    public static ArtArchive Open(string dataPath, string? indexPath = null) =>
        Path.GetExtension(dataPath).Equals(".uop", StringComparison.OrdinalIgnoreCase)
            ? new(UopArchive.Open(dataPath)) : new(ArtMulReader.Open(indexPath ?? throw new ArgumentException("artidx.mul is required."), dataPath));
    public DecodedImage Read(int id, bool land)
    {
        if (id < 0 || id > 0xFFFF || land && id >= 0x4000) throw new KeyNotFoundException("Art ID is out of range.");
        if (mul is not null) return land ? mul.ReadLand(id) : mul.ReadItem(id);
        int slot = land ? id : id + 0x4000;
        byte[] data = uop!.Read(UopNameHash.HashFileName($"build/artlegacymul/{slot:D8}.tga"));
        return land ? LandDecoder.Decode(data) : StaticArtDecoder.Decode(data);
    }
    public void Dispose() { mul?.Dispose(); uop?.Dispose(); }
}
