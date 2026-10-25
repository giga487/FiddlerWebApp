namespace Fiddler.Formats.Gumps;

public sealed class GumpDocument : IGumpArchive
{
    private readonly IGumpArchive source;
    private readonly Dictionary<int, GumpRecord> edits = [];
    public IReadOnlyList<MulEntry> Entries { get; private set; }
    public int DirtyCount => edits.Count;
    public bool HasChanges => edits.Count > 0;
    public int SlotCount => Math.Max(source.SlotCount, Entries.Select(entry => entry.Id).DefaultIfEmpty(-1).Max() + 1);
    public bool IsModified(int id) => edits.ContainsKey(id);
    public IReadOnlyDictionary<int, GumpRecord> GetChanges() => edits.ToDictionary(pair => pair.Key, pair => pair.Value with { Data = pair.Value.Data.ToArray() });
    public string Format => source is GumpUopReader ? "UOP" : "MUL / IDX";

    public static GumpDocument Open(string path, string? indexPath = null)
    {
        var source = GumpArchive.Open(path, indexPath);
        try { return new GumpDocument(source); }
        catch { source.Dispose(); throw; }
    }

    public GumpDocument(IGumpArchive source)
    {
        if (source.Entries.Any(entry => entry.Id < 0 || entry.Id >= GumpUopReader.MaxSlots))
            throw new InvalidDataException("The archive contains IDs beyond the supported Gump limit.");
        this.source = source;
        Entries = source.Entries;
    }

    public GumpRecord ReadRecord(int id) => edits.TryGetValue(id, out var record) ? record : source.ReadRecord(id);
    public DecodedImage Read(int id) => ReadRecord(id).Decode();

    public void Put(int id, GumpRecord record)
    {
        if (id < 0 || id >= GumpUopReader.MaxSlots) throw new ArgumentOutOfRangeException(nameof(id), "Gump ID must be between 0 and 0x11FFF.");
        _ = record.Decode(); // Validate before making a change visible.
        if (record.Data.Length > 16 * 1024 * 1024) throw new InvalidDataException("Gump record exceeds 16 MiB.");
        long pixels = edits.Where(pair => pair.Key != id).Sum(pair => (long)pair.Value.Width * pair.Value.Height) + (long)record.Width * record.Height;
        long bytes = edits.Where(pair => pair.Key != id).Sum(pair => (long)pair.Value.Data.Length) + record.Data.Length;
        if (pixels > 16_000_000 || bytes > 64 * 1024 * 1024 || !edits.ContainsKey(id) && edits.Count >= 512)
            throw new InvalidDataException("Too many changes in memory: save or discard the draft.");
        edits[id] = record with { Data = record.Data.ToArray() };
        Refresh();
    }

    public void Undo(int id) { edits.Remove(id); Refresh(); }
    public void Discard() { edits.Clear(); Entries = source.Entries; }
    private void Refresh()
    {
        var entries = source.Entries.ToDictionary(entry => entry.Id);
        foreach (var (id, record) in edits)
            entries[id] = new MulEntry(AssetKind.Gump, id, -1, record.Data.Length, record.Width << 16 | record.Height);
        Entries = Array.AsReadOnly(entries.Values.OrderBy(entry => entry.Id).ToArray());
    }

    public string ExportMul(string parentDirectory, CancellationToken token = default)
    {
        if (!Path.IsPathFullyQualified(parentDirectory)) throw new ArgumentException("Enter an absolute output folder path on the server.");
        var parent = Directory.CreateDirectory(Path.GetFullPath(parentDirectory));
        string key = Guid.NewGuid().ToString("N");
        string stage = Path.Combine(parent.FullName, ".gump-stage-" + key);
        string destination = Path.Combine(parent.FullName, "gump-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + key[..8]);
        if (Directory.Exists(stage) || Directory.Exists(destination)) throw new IOException("The export folder already exists.");
        Directory.CreateDirectory(stage);
        try
        {
            using (var data = new BinaryWriter(new FileStream(Path.Combine(stage, "gumpart.mul"), FileMode.CreateNew)))
            using (var index = new BinaryWriter(new FileStream(Path.Combine(stage, "gumpidx.mul"), FileMode.CreateNew)))
            {
                var ids = Entries.Select(entry => entry.Id).ToHashSet();
                int slots = Math.Min(GumpUopReader.MaxSlots, Math.Max(0x10000, Math.Max(source.SlotCount, ids.Count == 0 ? 0 : ids.Max() + 1)));
                for (int id = 0; id < slots; id++)
                {
                    token.ThrowIfCancellationRequested();
                    if (!ids.Contains(id)) { index.Write(-1); index.Write(0); index.Write(0); continue; }
                    var record = ReadRecord(id);
                    _ = record.Decode();
                    if (record.Data.Length > 16 * 1024 * 1024) throw new InvalidDataException("Gump record exceeds 16 MiB.");
                    if (data.BaseStream.Position > int.MaxValue - record.Data.Length) throw new InvalidDataException("MUL exceeds the 32-bit offset limit.");
                    index.Write((int)data.BaseStream.Position); index.Write(record.Data.Length); index.Write(record.Width << 16 | record.Height);
                    data.Write(record.Data);
                }
            }
            Directory.Move(stage, destination); // Publish the complete pair together in a new directory.
            return destination;
        }
        finally
        {
            if (Directory.Exists(stage))
            {
                foreach (string name in new[] { "gumpart.mul", "gumpidx.mul" }) File.Delete(Path.Combine(stage, name));
                Directory.Delete(stage);
            }
        }
    }
    public void Dispose() => source.Dispose();
}
