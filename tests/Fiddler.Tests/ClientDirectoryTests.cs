using Fiddler.Formats.Catalog;
using Fiddler.Formats.Gumps;
using FiddlerWebApp.Services;
using Xunit;

namespace Fiddler.Tests;

public sealed class ClientDirectoryTests
{
    [Fact]
    public void CatalogCoversEveryKnownFileExactlyOnce()
    {
        var grouped = ClientFileCatalog.Groups.SelectMany(g => g.Alternatives.SelectMany(a => a)).ToArray();
        Assert.Equal(ClientFileCatalog.ExpectedFiles.Order(), grouped.Order());
        Assert.Equal(grouped.Length, grouped.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains("map5legacymul.uop", grouped);
        Assert.Contains("unifont12.mul", grouped);
        Assert.Contains("anim6.idx", grouped);
        Assert.Contains("cliloc.custom2", grouped);
    }

    [Fact]
    public void EmptyFolderReportsMissingResourcesWithoutClaimingItCanBeViewed()
    {
        using var folder = new Folder();
        var report = new ClientDirectoryScanner().Scan(folder.Root);
        Assert.False(report.CanView);
        Assert.All(report.Groups, group => Assert.Equal(ClientGroupStatus.Missing, group.Status));
        Assert.All(report.Files, file => Assert.Equal(ClientFileStatus.Missing, file.Status));
        Assert.All(report.Files, file =>
        {
            Assert.NotNull(file.Guidance);
            Assert.False(string.IsNullOrWhiteSpace(file.Guidance.Purpose));
            Assert.False(string.IsNullOrWhiteSpace(file.Guidance.Impact));
        });
    }

    [Fact]
    public void MissingGumpIndexExplainsTheFeatureAndCompleteArchiveAlternatives()
    {
        using var folder = new Folder(); folder.Write("gumpart.mul", [1]);
        var report = new ClientDirectoryScanner().Scan(folder.Root);
        var missing = report.Files.Single(file => file.Name == "gumpidx.mul");
        Assert.Equal(ClientFileImportance.RequiredForFeature, missing.Guidance!.Importance);
        Assert.Contains("locations and dimensions", missing.Guidance.Purpose);
        Assert.Contains("gumpidx.mul + gumpart.mul or gumpartlegacymul.uop", missing.Guidance.Impact);
        Assert.Contains("Gump cannot be viewed", missing.Guidance.Impact);
    }

    [Fact]
    public void MissingAlternativeDoesNotClaimAFeatureIsBlockedWhenMulWorks()
    {
        using var folder = new Folder();
        folder.Write("gumpidx.mul", new byte[12]); folder.Write("gumpart.mul", [1]);
        var report = new ClientDirectoryScanner().Scan(folder.Root);
        var uop = report.Files.Single(file => file.Name == "gumpartlegacymul.uop");
        Assert.Equal(ClientFileImportance.CoveredByAlternative, uop.Guidance!.Importance);
        Assert.Contains("gumpidx.mul + gumpart.mul", uop.Guidance.Impact);
        Assert.Contains("No additional file is needed", uop.Guidance.Impact);
    }

    [Fact]
    public void AnimationUopDoesNotHideTheMissingMulPlaybackRequirement()
    {
        using var folder = new Folder(); folder.Write("animationframe1.uop", [1]);
        var report = new ClientDirectoryScanner().Scan(folder.Root);
        var index = report.Files.Single(file => file.Name == "anim.idx");
        Assert.Equal(ClientFileStatus.Alternative, index.Status);
        Assert.Equal(ClientFileImportance.RequiredForFeature, index.Guidance!.Importance);
        Assert.Contains("UOP does not replace MUL playback", index.Guidance.Impact);
        Assert.Equal(ClientFileImportance.NotUsedByViewer, report.Files.Single(file => file.Name == "animationframe1.uop").Guidance!.Importance);
    }

    [Fact]
    public void OptionalStaticsAndUnusedDataExplainTheirDifferentConsequences()
    {
        using var folder = new Folder(); var report = new ClientDirectoryScanner().Scan(folder.Root);
        var statics = report.Files.Single(file => file.Name == "statics0.mul").Guidance!;
        Assert.Equal(ClientFileImportance.Optional, statics.Importance);
        Assert.Contains("Terrain can still be viewed", statics.Impact);
        Assert.Contains("omit static objects", statics.Impact);
        Assert.Equal(ClientFileImportance.NotUsedByViewer, report.Files.Single(file => file.Name == "client.exe").Guidance!.Importance);
        Assert.Contains("does not apply these patches", report.Files.Single(file => file.Name == "mapdif0.mul").Guidance!.Impact);
        Assert.Contains("does not block", report.Files.Single(file => file.Name == "hues.mul").Guidance!.Impact);
    }

    [Fact]
    public void ArtUopExplainsTheDifferenceBetweenCatalogAndSceneRequirements()
    {
        using var folder = new Folder(); folder.Write("artlegacymul.uop", [1]);
        var report = new ClientDirectoryScanner().Scan(folder.Root);
        var index = report.Files.Single(file => file.Name == "artidx.mul");
        Assert.Equal(ClientFileImportance.RequiredForFeature, index.Guidance!.Importance);
        Assert.Contains("Art catalog requires art.mul + artidx.mul", index.Guidance.Impact);
        Assert.Contains("scene images require either", index.Guidance.Impact);
    }

    [Fact]
    public void MulPairIsAvailableAndMissingUopIsCoveredNotAnError()
    {
        using var folder = new Folder();
        folder.Write("ARTIDX.MUL", new byte[12]);
        folder.Write("Art.Mul", new byte[2024]);
        folder.Write("custom-assets.json", [1]);
        var report = new ClientDirectoryScanner().Scan(folder.Root);
        var art = report.Groups.Single(group => group.Definition.Name == "Land tiles and items");
        Assert.Equal(ClientGroupStatus.Available, art.Status);
        Assert.True(art.CanView);
        Assert.True(report.CanView);
        Assert.Equal(ClientFileStatus.Alternative, art.Files.Single(file => file.Name == "artlegacymul.uop").Status);
        Assert.Equal("custom-assets.json", Assert.Single(report.OtherFiles));
    }

    [Fact]
    public void UopIsRecognizedButDoesNotClaimCurrentViewerSupport()
    {
        using var folder = new Folder();
        folder.Write("artlegacymul.uop", new byte[28]);
        var report = new ClientDirectoryScanner().Scan(folder.Root);
        var art = report.Groups.Single(group => group.Definition.Name == "Land tiles and items");
        Assert.Equal(ClientGroupStatus.Available, art.Status);
        Assert.False(art.CanView);
        Assert.False(report.CanView);
        Assert.All(art.Files.Where(file => file.Name != "artlegacymul.uop"), file => Assert.Equal(ClientFileStatus.Alternative, file.Status));
    }

    [Fact]
    public void AnimationOnlyDirectoryIsRecognizedAsViewable()
    {
        using var folder = new Folder();
        folder.Write("anim.idx", new byte[12]);
        folder.Write("anim.mul", AnimationTests.Fixture());
        var report = new ClientDirectoryScanner().Scan(folder.Root);
        Assert.True(report.CanView);
        Assert.True(report.Groups.Single(group => group.Definition.Name == "Animations 1").CanView);
    }

    [Fact]
    public void GumpUopIsRecognizedAsViewable()
    {
        using var folder = new Folder();
        var record = GumpEncoder.Encode(Fiddler.Formats.DecodedImage.FromArgb(1, 1, [unchecked((int)0xFFFF0000)]));
        GumpEditingTests.WriteUop(Path.Combine(folder.Root, "gumpartLegacyMUL.uop"), [(1, record)], 4, 1);
        var report = new ClientDirectoryScanner().Scan(folder.Root);
        Assert.True(report.CanView);
        Assert.True(report.Groups.Single(group => group.Definition.Name == "Gump").CanView);
    }

    [Fact]
    public void MissingPartnerIsFlaggedAsIncompleteWithExactFilename()
    {
        using var folder = new Folder();
        folder.Write("gumpart.mul", [1, 2]);
        var report = new ClientDirectoryScanner().Scan(folder.Root);
        var gumps = report.Groups.Single(group => group.Definition.Name == "Gump");
        Assert.Equal(ClientGroupStatus.Incomplete, gumps.Status);
        Assert.Equal(ClientFileStatus.Missing, gumps.Files.Single(file => file.Name == "gumpidx.mul").Status);
        Assert.False(gumps.CanView);
    }

    [Fact]
    public void EmptyFilesAndMalformedIndexesAreNotUsable()
    {
        using var folder = new Folder();
        folder.Write("artidx.mul", [1, 2, 3]);
        folder.Write("art.mul", []);
        var report = new ClientDirectoryScanner().Scan(folder.Root);
        var art = report.Groups.Single(group => group.Definition.Name == "Land tiles and items");
        Assert.Equal(ClientGroupStatus.Invalid, art.Status);
        Assert.Equal(ClientFileStatus.Invalid, art.Files.Single(file => file.Name == "artidx.mul").Status);
        Assert.Equal(ClientFileStatus.Empty, art.Files.Single(file => file.Name == "art.mul").Status);
        Assert.False(report.CanView);
    }

    [Fact]
    public void EmptyStaticDataIsValidAndPatchDependenciesAreGrouped()
    {
        using var folder = new Folder();
        folder.Write("staidx0.mul", new byte[12]);
        folder.Write("statics0.mul", []);
        folder.Write("stadif0.mul", [1]);
        var report = new ClientDirectoryScanner().Scan(folder.Root);
        Assert.Equal(ClientGroupStatus.Available, report.Groups.Single(group => group.Definition.Name == "Map statics 0").Status);
        var patch = report.Groups.Single(group => group.Definition.Name == "Static patches 0");
        Assert.Equal(ClientGroupStatus.Incomplete, patch.Status);
        Assert.Equal(new[] { "stadifi0.mul", "stadifl0.mul" }, patch.Files.Where(file => file.Status == ClientFileStatus.Missing).Select(file => file.Name).Order());
    }

    [Fact]
    public void ScannerDoesNotRecurseAndDoesNotModifyFiles()
    {
        using var folder = new Folder();
        string nested = Directory.CreateDirectory(Path.Combine(folder.Root, "nested")).FullName;
        File.WriteAllBytes(Path.Combine(nested, "art.mul"), [1]);
        folder.Write("tiledata.mul", [12, 34, 56]);
        var report = new ClientDirectoryScanner().Scan(folder.Root);
        Assert.Equal(ClientFileStatus.Missing, report.Files.Single(file => file.Name == "art.mul").Status);
        Assert.Equal(new byte[] { 12, 34, 56 }, File.ReadAllBytes(Path.Combine(folder.Root, "tiledata.mul")));
    }

    [Fact]
    public void CancellationAndInvalidDirectoriesAreHandled()
    {
        using var folder = new Folder();
        var scanner = new ClientDirectoryScanner();
        Assert.Throws<ArgumentException>(() => scanner.Scan("relative/client"));
        Assert.Throws<ArgumentException>(() => scanner.Scan(""));
        Assert.Throws<DirectoryNotFoundException>(() => scanner.Scan(Path.Combine(folder.Root, "missing")));
        Assert.Throws<OperationCanceledException>(() => scanner.Scan(folder.Root, new CancellationToken(canceled: true)));
    }

    [Fact]
    public void SelectedDirectorySurvivesRestartAndFailedSavePreservesSelection()
    {
        using var folder = new Folder();
        string storage = Path.Combine(folder.Root, "settings", "client-directory.json");
        var settings = new ClientDirectorySettings(storage, "");
        settings.Save(folder.Root);
        Assert.Equal(folder.Root, settings.Directory);
        Assert.Equal(folder.Root, new ClientDirectorySettings(storage, "different-default").Directory);
        Assert.Throws<DirectoryNotFoundException>(() => settings.Save(Path.Combine(folder.Root, "missing")));
        Assert.Equal(folder.Root, settings.Directory);
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(storage)!, "*.tmp"));
    }

    private sealed class Folder : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("fiddler-directory-").FullName;
        public void Write(string name, byte[] bytes) => File.WriteAllBytes(Path.Combine(Root, name), bytes);
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
