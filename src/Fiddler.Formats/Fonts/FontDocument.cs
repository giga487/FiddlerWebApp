using System.Buffers.Binary;
using Fiddler.Formats.Editing;
using Fiddler.Formats.Hues;

namespace Fiddler.Formats.Fonts;

public sealed record FontGlyph(int Font, int Code, int Width, int Height, sbyte XOffset, sbyte YOffset, byte Unknown, byte[] Data);

public sealed class FontDocument
{
    private readonly Dictionary<(int Font, int Code), FontGlyph> original = [], edits = [];
    private readonly List<byte> headers = [];
    public string FileName { get; }
    public bool Unicode { get; }
    public int DirtyCount => edits.Count;
    public IEnumerable<int> Fonts => Unicode ? [0] : headers.Select((value, index) => (value, index)).Where(p => p.value != 0).Select(p => p.index);
    public IEnumerable<FontGlyph> Glyphs(int font) => original.Keys.Concat(edits.Keys).Distinct().Where(k => k.Font == font).OrderBy(k => k.Code)
        .Select(k => edits.TryGetValue(k, out var glyph) ? glyph : original[k]).Select(glyph => glyph with { Data = glyph.Data.ToArray() });

    public static FontDocument Open(string path) => new(path, ResourceExport.Read(path, 64 * 1024 * 1024));
    private FontDocument(string path, byte[] data)
    {
        FileName = Path.GetFileName(path); Unicode = FileName.StartsWith("unifont", StringComparison.OrdinalIgnoreCase);
        if (Unicode)
        {
            if (data.Length < 0x40000) throw new InvalidDataException("Unicode font requires a 65,536-entry offset table.");
            for (int code = 0; code < 0x10000; code++)
            {
                int offset = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(code * 4));
                if (offset == 0 || offset == -1) continue;
                if (offset < 0x40000 || offset > data.Length - 4) throw new InvalidDataException("Unicode glyph has an invalid offset.");
                int width = data[offset + 2], height = data[offset + 3], length = (width + 7) / 8 * height;
                if (offset + 4 > data.Length - length) throw new InvalidDataException("Unicode glyph bitmap is truncated.");
                original[(0, code)] = new(0, code, width, height, unchecked((sbyte)data[offset]), unchecked((sbyte)data[offset + 1]), 0, data.AsSpan(offset + 4, length).ToArray());
            }
        }
        else
        {
            int offset = 0;
            while (offset < data.Length)
            {
                if (headers.Count >= 20) throw new InvalidDataException("ASCII font contains more than 20 fonts or trailing data.");
                int font = headers.Count; byte header = data[offset++]; headers.Add(header);
                if (header == 0) continue;
                for (int code = 32; code < 256; code++)
                {
                    if (offset > data.Length - 3) throw new InvalidDataException("ASCII glyph header is truncated.");
                    int width = data[offset], height = data[offset + 1], length = width * height * 2;
                    byte unknown = data[offset + 2]; offset += 3;
                    if (offset > data.Length - length) throw new InvalidDataException("ASCII glyph bitmap is truncated.");
                    original[(font, code)] = new(font, code, width, height, 0, 0, unknown, data.AsSpan(offset, length).ToArray());
                    offset += length;
                }
            }
            if (headers.Count == 0 || !headers.Any(header => header != 0)) throw new InvalidDataException("ASCII font file contains no active fonts.");
        }
    }
    public FontGlyph Read(int font, int code)
    {
        if (!Fonts.Contains(font) || code < (Unicode ? 0 : 32) || code >= (Unicode ? 0x10000 : 256)) throw new ArgumentOutOfRangeException(nameof(code));
        var glyph = edits.GetValueOrDefault((font, code)) ?? original.GetValueOrDefault((font, code)) ?? new(font, code, 0, 0, 0, 0, 0, []);
        return glyph with { Data = glyph.Data.ToArray() };
    }
    public DecodedImage Image(int font, int code)
    {
        var glyph = Read(font, code);
        if (glyph.Width == 0 || glyph.Height == 0) return DecodedImage.FromArgb(1, 1, [0]);
        var pixels = new int[glyph.Width * glyph.Height];
        for (int y = 0; y < glyph.Height; y++)
            for (int x = 0; x < glyph.Width; x++)
            {
                if (Unicode)
                {
                    if ((glyph.Data[y * ((glyph.Width + 7) / 8) + x / 8] & (0x80 >> (x % 8))) != 0) pixels[y * glyph.Width + x] = unchecked((int)0xFF000000);
                }
                else
                {
                    ushort color = BinaryPrimitives.ReadUInt16LittleEndian(glyph.Data.AsSpan((y * glyph.Width + x) * 2));
                    if (color != 0 && (color & 0x8000) == 0) pixels[y * glyph.Width + x] = HuePalette.ToArgb(color);
                }
            }
        return DecodedImage.FromArgb(glyph.Width, glyph.Height, pixels);
    }
    public void Put(int font, int code, DecodedImage image, sbyte xOffset = 0, sbyte yOffset = 0)
    {
        if (image.Width > 255 || image.Height > 255) throw new ArgumentException("Font glyphs must fit 255 × 255 pixels.");
        var previous = Read(font, code);
        byte[] data = new byte[Unicode ? (image.Width + 7) / 8 * image.Height : image.Width * image.Height * 2];
        for (int y = 0; y < image.Height; y++)
            for (int x = 0; x < image.Width; x++)
            {
                int pixel = image.Pixels.Span[y * image.Width + x];
                if ((uint)pixel >> 24 < 128) continue;
                if (Unicode) data[y * ((image.Width + 7) / 8) + x / 8] |= (byte)(0x80 >> (x % 8));
                else
                {
                    ushort color = HuePalette.FromRgb((pixel >> 16) & 255, (pixel >> 8) & 255, pixel & 255);
                    BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan((y * image.Width + x) * 2), color == 0 ? (ushort)1 : color);
                }
            }
        PutRecord(previous with { Width = image.Width, Height = image.Height, XOffset = xOffset, YOffset = yOffset, Data = data });
    }
    public void SetOffsets(int font, int code, sbyte x, sbyte y)
    { if (!Unicode) throw new InvalidOperationException("Offsets are available only for Unicode fonts."); PutRecord(Read(font, code) with { XOffset = x, YOffset = y }); }
    public void Clear(int font, int code) => PutRecord(Read(font, code) with { Width = 0, Height = 0, Data = [] });
    private void PutRecord(FontGlyph glyph)
    {
        if (!edits.ContainsKey((glyph.Font, glyph.Code)) && edits.Count >= 1024 || edits.Where(p => p.Key != (glyph.Font, glyph.Code)).Sum(p => (long)p.Value.Data.Length) + glyph.Data.Length > 64 * 1024 * 1024)
            throw new InvalidOperationException("Export or discard the font draft before editing more glyphs.");
        edits[(glyph.Font, glyph.Code)] = glyph;
    }
    public void Undo(int font, int code) => edits.Remove((font, code));
    public void Discard() => edits.Clear();
    public DecodedImage RenderText(int font, string text)
    {
        if (text.Length > 512) throw new ArgumentException("Text previews support at most 512 UTF-16 characters.");
        var positions = new List<(DecodedImage Image, int X, int Y)>();
        int lineHeight = Glyphs(font).Select(g => g.Height + Math.Abs((int)g.YOffset)).DefaultIfEmpty(1).Max() + 2;
        int x = 0, y = 0, minX = 0, minY = 0, maxX = 1, maxY = 1;
        foreach (char code in text)
        {
            if (code == '\r') continue;
            if (code == '\n') { x = 0; y += lineHeight; maxY = Math.Max(maxY, y + lineHeight); continue; }
            if (!Unicode && code is < ' ' or > '\u00ff') continue;
            var glyph = Read(font, code);
            int px = x + glyph.XOffset, py = Unicode ? y + glyph.YOffset : y + lineHeight - 2 - glyph.Height;
            if (glyph.Width > 0 && glyph.Height > 0)
            {
                positions.Add((Image(font, code), px, py));
                minX = Math.Min(minX, px); minY = Math.Min(minY, py);
                maxX = Math.Max(maxX, px + glyph.Width); maxY = Math.Max(maxY, py + glyph.Height);
            }
            x += Math.Max(1, glyph.Width + glyph.XOffset);
            maxX = Math.Max(maxX, x);
        }
        int width = maxX - minX + 4, height = maxY - minY + 4;
        if (width > 4096 || height > 4096 || (long)width * height > 4_000_000) throw new ArgumentException("Text preview exceeds the image limits.");
        var pixels = new int[width * height];
        foreach (var position in positions)
            for (int row = 0; row < position.Image.Height; row++)
                for (int col = 0; col < position.Image.Width; col++)
                {
                    int pixel = position.Image.Pixels.Span[row * position.Image.Width + col];
                    if ((uint)pixel >> 24 != 0) pixels[(position.Y - minY + 2 + row) * width + position.X - minX + 2 + col] = pixel;
                }
        return DecodedImage.FromArgb(width, height, pixels);
    }
    public string Export(string parent) => ResourceExport.Write(parent, "fonts", folder =>
    {
        using var writer = new BinaryWriter(File.Create(Path.Combine(folder, FileName)));
        if (Unicode)
        {
            writer.Write(new byte[0x40000]);
            foreach (var glyph in Glyphs(0))
            {
                int offset = checked((int)writer.BaseStream.Length);
                writer.BaseStream.Position = glyph.Code * 4;
                writer.Write(offset); writer.BaseStream.Position = offset;
                writer.Write(glyph.XOffset); writer.Write(glyph.YOffset); writer.Write((byte)glyph.Width); writer.Write((byte)glyph.Height); writer.Write(glyph.Data);
            }
        }
        else
        {
            for (int font = 0; font < headers.Count; font++)
            {
                writer.Write(headers[font]);
                if (headers[font] == 0) continue;
                foreach (var glyph in Glyphs(font))
                { writer.Write((byte)glyph.Width); writer.Write((byte)glyph.Height); writer.Write(glyph.Unknown); writer.Write(glyph.Data); }
            }
        }
    });
}
