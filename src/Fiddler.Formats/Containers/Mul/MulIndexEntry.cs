namespace Fiddler.Formats.Containers.Mul;

/// <summary>A raw 12-byte IDX record. Interpretation belongs to the resource reader.</summary>
public sealed record MulIndexEntry(int Slot, int Offset, int Length, int Extra);
