using System.Text.Json;
using Fiddler.Formats;
using Fiddler.Formats.Fonts;
using Fiddler.Formats.Hues;
using Fiddler.Formats.Localization;
using Fiddler.Formats.Metadata;
using Fiddler.Graphics;
using Fiddler.Graphics.Gumps;
using UOAS.Contracts;

namespace UOAS.Application.Collaboration;

internal sealed class NativeDocument
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly DocumentKind kind;
    private readonly HueDocument? hues;
    private readonly ClilocDocument? cliloc;
    private readonly FontDocument? font;
    private readonly TileDataDocument? tiledata;
    private sealed record HueData(string Name, ushort[] Colors);
    private sealed record FontData(int Font, int Code, sbyte XOffset, sbyte YOffset, string? ImagePng);
    public NativeDocument(DocumentKind kind, string path)
    {
        this.kind = kind;
        switch (kind)
        {
            case DocumentKind.Hue: hues = HueDocument.Open(path); break;
            case DocumentKind.Cliloc: cliloc = ClilocDocument.Open(path); break;
            case DocumentKind.Font: font = FontDocument.Open(path); break;
            default: tiledata = TileDataDocument.Open(path); break;
        }
    }
    private AssetKind TileKind => kind == DocumentKind.TileDataLand ? AssetKind.Land : AssetKind.Item;
    public DecodedImage RenderText(int index, string text) => (font ?? throw new InvalidOperationException("Open a font document first.")).RenderText(index, text);
    public IEnumerable<int> Ids => kind switch
    {
        DocumentKind.Hue => Enumerable.Range(1, hues!.Count),
        DocumentKind.Cliloc => cliloc!.Entries.Select(entry => entry.Id),
        DocumentKind.Font => font!.Fonts.SelectMany(index => Enumerable.Range(font.Unicode ? 0 : 32, font.Unicode ? 65536 : 224).Select(code => index * 65536 + code)),
        DocumentKind.TileDataLand => Enumerable.Range(0, 0x4000),
        _ => Enumerable.Range(0, tiledata!.ItemCount)
    };
    public JsonElement Read(int id)
    {
        if (id < 0) throw new ArgumentOutOfRangeException(nameof(id));
        object record = kind switch
        {
            DocumentKind.Hue => id == hues!.Count + 1 ? new HueData("New hue", new ushort[32]) : new HueData(hues.Read(id).Name, hues.Read(id).Colors.ToArray()),
            DocumentKind.Cliloc => cliloc!.Entries.FirstOrDefault(entry => entry.Id == id) ?? new ClilocEntry(id, 0, ""),
            DocumentKind.Font => FontRecord(id),
            _ => tiledata!.Read(TileKind, id)
        };
        return JsonSerializer.SerializeToElement(record, Json);
    }
    private FontData FontRecord(int id)
    {
        var glyph = font!.Read(id / 65536, id % 65536);
        string? png = glyph.Width > 0 && glyph.Height > 0 ? Convert.ToBase64String(new SkiaAssetRenderer().RenderPng(font.Image(glyph.Font, glyph.Code))) : null;
        return new(glyph.Font, glyph.Code, glyph.XOffset, glyph.YOffset, png);
    }
    public void Put(int id, JsonElement value)
    {
        if (id < 0) throw new ArgumentOutOfRangeException(nameof(id));
        switch (kind)
        {
            case DocumentKind.Hue:
                var hue = value.Deserialize<HueData>(Json) ?? throw new ArgumentException("Provide a hue name and 32 RGB555 colors.");
                if (hue.Colors is null || hue.Colors.Length != 32) throw new ArgumentException("Provide exactly 32 hue colors.");
                if (id == hues!.Count + 1) hues.Add(hue.Name, hue.Colors); else hues.Put(id, hue.Name, hue.Colors);
                break;
            case DocumentKind.Cliloc:
                var text = value.Deserialize<ClilocEntry>(Json) ?? throw new ArgumentException("Provide a localized string record.");
                cliloc!.Put(text with { Id = id }); break;
            case DocumentKind.Font:
                var glyph = value.Deserialize<FontData>(Json) ?? throw new ArgumentException("Provide glyph metadata and an imagePng base64 string.");
                if (glyph.ImagePng is null) font!.Clear(id / 65536, id % 65536);
                else font!.Put(id / 65536, id % 65536, GumpImageImporter.ImportImage(Convert.FromBase64String(glyph.ImagePng)), glyph.XOffset, glyph.YOffset);
                break;
            default:
                var tile = value.Deserialize<TileMetadata>(Json) ?? throw new ArgumentException("Provide TileData metadata.");
                tiledata!.Put(tile with { Kind = TileKind, Id = id }); break;
        }
    }
    public string Export(string directory) => kind switch
    { DocumentKind.Hue => Path.GetDirectoryName(hues!.Export(directory))!, DocumentKind.Cliloc => cliloc!.Export(directory), DocumentKind.Font => font!.Export(directory), _ => tiledata!.Export(directory) };
}
