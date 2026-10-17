namespace Fiddler.Formats.Gumps;

public enum GumpDifference { Equal, Different, CurrentOnly, ReferenceOnly, Invalid }
public sealed record GumpComparison(int Id, GumpDifference Difference, string? Detail = null);

public static class GumpComparer
{
    public static IReadOnlyList<GumpComparison> Compare(GumpDocument current, IGumpArchive reference,
        CancellationToken token = default, IProgress<int>? progress = null)
    {
        var left = current.Entries.Select(entry => entry.Id).ToHashSet();
        var right = reference.Entries.Select(entry => entry.Id).ToHashSet();
        int[] ids = left.Union(right).Order().ToArray();
        var result = new List<GumpComparison>(ids.Length);
        for (int i = 0; i < ids.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            int id = ids[i];
            try
            {
                if (!left.Contains(id)) { _ = reference.Read(id); result.Add(new(id, GumpDifference.ReferenceOnly)); }
                else if (!right.Contains(id)) { _ = current.Read(id); result.Add(new(id, GumpDifference.CurrentOnly)); }
                else
                {
                    var a = current.Read(id); var b = reference.Read(id);
                    bool same = a.Width == b.Width && a.Height == b.Height && SamePixels(a.Pixels.Span, b.Pixels.Span);
                    result.Add(new(id, same ? GumpDifference.Equal : GumpDifference.Different));
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException)
            { result.Add(new(id, GumpDifference.Invalid, ex.Message)); }
            if (i % 32 == 0 || i == ids.Length - 1) progress?.Report((i + 1) * 100 / Math.Max(1, ids.Length));
        }
        return result.AsReadOnly();
    }
    private static bool SamePixels(ReadOnlySpan<int> a, ReadOnlySpan<int> b)
    {
        for (int i = 0; i < a.Length; i++)
            if (a[i] != b[i] && ((uint)a[i] >> 24 != 0 || (uint)b[i] >> 24 != 0)) return false;
        return true;
    }
}
