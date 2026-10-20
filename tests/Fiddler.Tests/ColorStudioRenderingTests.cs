using System.Text.RegularExpressions;
using Fiddler.Formats;
using Fiddler.Formats.Hues;
using Fiddler.Graphics;
using FiddlerWebApp.Components.Pages;
using FiddlerWebApp.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SkiaSharp;
using Xunit;

namespace Fiddler.Tests;

public sealed class ColorStudioRenderingTests
{
    [Fact]
    public async Task SelectedPaletteRendersAcrossBothSampleModesAndKeepsOriginals()
    {
        string folder = Path.Combine(Path.GetTempPath(), "fiddler-color-render-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var studio = new ColorStudioWorkspace();
            studio.Open(folder);
            studio.SelectedId = studio.Document!.Add("Test red", Enumerable.Range(0, 32).Select(i => (ushort)(i << 10)).ToArray());
            var image = DecodedImage.FromArgb(2, 1, [HuePalette.ToArgb(0x4210), HuePalette.ToArgb(0x03E0)]);
            studio.Add(AssetKind.Item, 100, image, HueMode.Full);
            studio.Add(AssetKind.Item, 100, image, HueMode.GrayPixelsOnly);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(studio);
            services.AddSingleton<MulWorkspace>();
            services.AddSingleton<SkiaAssetRenderer>();
            services.AddSingleton(new ClientDirectorySettings(Path.Combine(folder, "settings.json"), folder));
            services.AddSingleton<NavigationManager, TestNavigationManager>();
            await using var provider = services.BuildServiceProvider();
            await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
            async Task<string> Render() => await renderer.Dispatcher.InvokeAsync(async () =>
                (await renderer.RenderComponentAsync<ColorStudio>()).ToHtmlString());
            string html = await Render();
            Assert.Contains("Test red", html);
            Assert.Equal(2, Regex.Matches(html, "<article class=\"color-sample\"").Count);
            string[] images = Regex.Matches(html, "src=\"data:image/png;base64,([^\"]+)\"").Select(m => System.Net.WebUtility.HtmlDecode(m.Groups[1].Value)).ToArray();
            Assert.Equal(4, images.Length);
            using var original = SKBitmap.Decode(Convert.FromBase64String(images[0]));
            using var full = SKBitmap.Decode(Convert.FromBase64String(images[1]));
            using var partial = SKBitmap.Decode(Convert.FromBase64String(images[3]));
            Assert.Equal(SKColors.Lime, original.GetPixel(1, 0));
            Assert.Equal(SKColors.Black, full.GetPixel(1, 0));
            Assert.Equal(SKColors.Lime, partial.GetPixel(1, 0));
            Assert.Equal(new SKColor(132, 0, 0), partial.GetPixel(0, 0));
            studio.Document.Put(studio.SelectedId, "Test blue", Enumerable.Range(0, 32).Select(i => (ushort)i).ToArray());
            string changed = await Render();
            Assert.Contains("Test blue", changed);
            string[] changedImages = Regex.Matches(changed, "src=\"data:image/png;base64,([^\"]+)\"").Select(m => System.Net.WebUtility.HtmlDecode(m.Groups[1].Value)).ToArray();
            Assert.Equal(images[0], changedImages[0]);
            Assert.Equal(images[2], changedImages[2]);
            using var blue = SKBitmap.Decode(Convert.FromBase64String(changedImages[3]));
            Assert.Equal(new SKColor(0, 0, 132), blue.GetPixel(0, 0));
            Assert.Equal(SKColors.Lime, blue.GetPixel(1, 0));
        }
        finally { Directory.Delete(folder, true); }
    }

    private sealed class TestNavigationManager : NavigationManager
    {
        public TestNavigationManager() => Initialize("http://localhost/", "http://localhost/colors");
        protected override void NavigateToCore(string uri, bool forceLoad) => throw new NotSupportedException();
    }
}
