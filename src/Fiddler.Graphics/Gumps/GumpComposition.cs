using Fiddler.Formats;

namespace Fiddler.Graphics.Gumps;

public sealed record GumpPiece(int Key, int GumpId, DecodedImage Image, int X, int Y);

/// <summary>Pixel coordinates snapped to a grid; images always retain their original size.</summary>
public sealed class GumpComposition
{
    private readonly List<GumpPiece> pieces = [];
    private int nextKey;
    public IReadOnlyList<GumpPiece> Pieces => pieces.AsReadOnly();
    public int Width { get; private set; } = 768;
    public int Height { get; private set; } = 512;
    public int CellSize { get; private set; } = 32;

    public GumpPiece Add(int id, DecodedImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (pieces.Count >= 128 || pieces.Sum(p => (long)p.Image.Width * p.Image.Height) + (long)image.Width * image.Height > 16_000_000)
            throw new InvalidOperationException("Composizione piena: massimo 128 pezzi e 16 milioni di pixel.");
        var position = FindSpace(image, pieces, Width, Height, CellSize);
        var piece = new GumpPiece(++nextKey, id, image, position.X, position.Y);
        pieces.Add(piece);
        return piece;
    }

    public void Configure(int width, int height, int cellSize)
    {
        if (width < 64 || height < 64 || width > 2048 || height > 2048 || cellSize < 4 || cellSize > 256)
            throw new ArgumentException("Piano da 64 a 2048 px; celle da 4 a 256 px.");
        // Calculate first so a failed resize preserves the entire composition.
        var arranged = Arrange(pieces, width, height, cellSize);
        Width = width; Height = height; CellSize = cellSize;
        pieces.Clear(); pieces.AddRange(arranged);
    }

    public void AutoArrange()
    {
        var arranged = Arrange(pieces, Width, Height, CellSize);
        pieces.Clear(); pieces.AddRange(arranged);
    }

    public void ReplaceImage(int key, DecodedImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        int index = pieces.FindIndex(p => p.Key == key);
        if (index < 0) throw new KeyNotFoundException("Pezzo non presente.");
        if (image.Width > Width || image.Height > Height ||
            pieces.Where(p => p.Key != key).Sum(p => (long)p.Image.Width * p.Image.Height) + (long)image.Width * image.Height > 16_000_000)
            throw new InvalidOperationException("L'immagine aggiornata supera i limiti della composizione.");
        pieces[index] = pieces[index] with { Image = image };
        Move(key, pieces[index].X, pieces[index].Y);
    }

    public void Move(int key, double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y)) throw new ArgumentException("Coordinate non valide.");
        int index = pieces.FindIndex(p => p.Key == key);
        if (index < 0) throw new KeyNotFoundException("Pezzo non presente.");
        var piece = pieces[index];
        int Snap(double value, int limit) => (int)Math.Clamp(Math.Round(value / CellSize, MidpointRounding.AwayFromZero), 0, limit / CellSize) * CellSize;
        pieces[index] = piece with { X = Snap(x, Width - piece.Image.Width), Y = Snap(y, Height - piece.Image.Height) };
    }

    public GumpPiece? HitTest(double x, double y) => pieces.LastOrDefault(p =>
        x >= p.X && y >= p.Y && x < p.X + p.Image.Width && y < p.Y + p.Image.Height);
    public void Remove(int key) => pieces.RemoveAll(p => p.Key == key);
    public void Clear() => pieces.Clear();

    private static List<GumpPiece> Arrange(IEnumerable<GumpPiece> source, int width, int height, int cell)
    {
        var result = new List<GumpPiece>();
        foreach (var piece in source)
        {
            var pos = FindSpace(piece.Image, result, width, height, cell);
            result.Add(piece with { X = pos.X, Y = pos.Y });
        }
        return result;
    }

    private static (int X, int Y) FindSpace(DecodedImage image, IReadOnlyList<GumpPiece> occupied, int width, int height, int cell)
    {
        for (int y = 0; y <= height - image.Height; y += cell)
            for (int x = 0; x <= width - image.Width; x += cell)
                if (!occupied.Any(p => x < p.X + p.Image.Width && x + image.Width > p.X && y < p.Y + p.Image.Height && y + image.Height > p.Y))
                    return (x, y);
        throw new InvalidOperationException("Spazio insufficiente nella griglia: aumenta il piano o rimuovi un pezzo.");
    }
}
