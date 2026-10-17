namespace Fiddler.Formats;

public enum AssetKind { Land, Item, Gump }

public sealed record MulEntry(AssetKind Kind, int Id, int Offset, int Length, int Extra);
