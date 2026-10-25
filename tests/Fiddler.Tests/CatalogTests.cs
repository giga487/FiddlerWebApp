using Fiddler.Formats;
using Fiddler.Formats.Art;
using Fiddler.Formats.Gumps;
using FiddlerWebApp.Services;
using Xunit;

namespace Fiddler.Tests;

public sealed class CatalogTests
{
    [Fact]
    public void GumpSlotsIncludeEmptyIdsAndReflectDraftsWithoutChangingTheSource()
    {
        using var files = new ClientResourceEditingTests.Files();
        var record = GumpEncoder.Encode(DecodedImage.FromArgb(1, 1, [unchecked((int)0xFFFF0000)]));
        files.Write("gumpart.mul", record.Data);
        using (var index = new BinaryWriter(File.Create(Path.Combine(files.Root, "gumpidx.mul"))))
            for (int id = 0; id < 4; id++) { index.Write(id == 1 ? 0 : -1); index.Write(id == 1 ? record.Data.Length : 0); index.Write(id == 1 ? 0x10001 : 0); }
        using var workspace = new MulWorkspace(); workspace.Open(files.Root);
        Assert.Equal(new[] { 0, 1, 2, 3 }, workspace.CatalogEntries(AssetKind.Gump).Select(entry => entry.Id));
        Assert.Equal(1, Assert.Single(workspace.CatalogEntries(AssetKind.Gump, includeEmpty: false)).Id);
        workspace.Gumps!.Put(3, record);
        Assert.True(workspace.CatalogEntries(AssetKind.Gump)[3].Length > 0);
        workspace.Gumps.Undo(3);
        Assert.Equal(0, workspace.CatalogEntries(AssetKind.Gump)[3].Length);
        Assert.Equal(record.Data, File.ReadAllBytes(Path.Combine(files.Root, "gumpart.mul")));
    }

    [Fact]
    public void ArtCatalogKeepsLandAndItemSlotBoundariesAndDraftRemovals()
    {
        using var files = new ClientResourceEditingTests.Files();
        byte[] art = ArtEncoder.Item(DecodedImage.FromArgb(1, 1, [unchecked((int)0xFFFF0000)]));
        files.Write("art.mul", art);
        using (var index = new BinaryWriter(File.Create(Path.Combine(files.Root, "artidx.mul"))))
            for (int slot = 0; slot < 0x4002; slot++) { index.Write(slot == 0x4000 ? 0 : -1); index.Write(slot == 0x4000 ? art.Length : 0); index.Write(0); }
        using var workspace = new MulWorkspace(); workspace.Open(files.Root);
        Assert.Equal(0x4000, workspace.CatalogEntries(AssetKind.Land).Count);
        var items = workspace.CatalogEntries(AssetKind.Item);
        Assert.Equal(new[] { 0, 1 }, items.Select(entry => entry.Id));
        Assert.True(items[0].Length > 0); Assert.Equal(0, items[1].Length);
        workspace.Art!.Remove(AssetKind.Item, 0);
        Assert.All(workspace.CatalogEntries(AssetKind.Item), entry => Assert.Equal(0, entry.Length));
        Assert.Empty(workspace.CatalogEntries(AssetKind.Item, includeEmpty: false));
        workspace.Art.Put(AssetKind.Item, 3, DecodedImage.FromArgb(1, 1, [unchecked((int)0xFFFF0000)]));
        Assert.Equal(4, workspace.CatalogEntries(AssetKind.Item).Count);
        Assert.Equal(0x4000, workspace.CatalogEntries(AssetKind.Land).Count);
    }
}
