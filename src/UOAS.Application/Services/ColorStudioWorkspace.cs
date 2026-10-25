using Fiddler.Formats;
using Fiddler.Formats.Hues;

namespace FiddlerWebApp.Services;

public sealed class ColorSample
{
    public required int Key { get; init; }
    public required AssetKind Kind { get; init; }
    public required int AssetId { get; init; }
    public required DecodedImage Image { get; init; }
    public HueMode Mode { get; set; }
}

/// <summary>Each user's palette drafts and comparison board live in their own Blazor scope.</summary>
public sealed class ColorStudioWorkspace
{
    private readonly List<ColorSample> samples = [];
    private int nextKey;
    public HueDocument? Document { get; private set; }
    public string DirectoryPath { get; private set; } = "";
    public int SelectedId { get; set; }
    public IReadOnlyList<ColorSample> Samples => samples.AsReadOnly();

    public void Open(string directory, bool discard = false)
    {
        if (Document?.HasChanges == true && !discard)
            throw new InvalidOperationException("This library has hue drafts. Export any work you want to keep, then use Discard and reopen to switch libraries.");
        var folder = new DirectoryInfo(Path.GetFullPath(directory));
        if (!folder.Exists) throw new DirectoryNotFoundException("Configure an existing client folder first.");
        string? path = folder.EnumerateFiles().FirstOrDefault(f => f.Name.Equals("hues.mul", StringComparison.OrdinalIgnoreCase))?.FullName;
        var next = path is null ? HueDocument.Create() : HueDocument.Open(path);
        Document = next;
        DirectoryPath = folder.FullName;
        SelectedId = next.Count > 0 ? 1 : 0;
        samples.Clear();
    }

    public ColorSample Add(AssetKind kind, int id, DecodedImage image, HueMode mode)
    {
        if (kind is not (AssetKind.Item or AssetKind.Gump)) throw new ArgumentException("Choose an Item or Gump.");
        if (id < 0 || !Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(id));
        if (samples.Count >= 24 || samples.Sum(s => (long)s.Image.Width * s.Image.Height) + image.Pixels.Length > 8_000_000)
            throw new InvalidOperationException("The board supports up to 24 samples and 8 million pixels. Remove a sample before adding another.");
        var sample = new ColorSample { Key = ++nextKey, Kind = kind, AssetId = id, Image = image, Mode = mode };
        samples.Add(sample);
        return sample;
    }

    public void Remove(int key) => samples.RemoveAll(s => s.Key == key);
}
