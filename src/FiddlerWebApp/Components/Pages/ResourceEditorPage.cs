using System.Globalization;
using Fiddler.Graphics;
using Fiddler.Graphics.Gumps;
using FiddlerWebApp.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;

namespace FiddlerWebApp.Components.Pages;

public abstract class ResourceEditorPage : ComponentBase
{
    [Inject] protected ClientDirectorySettings Settings { get; set; } = null!;
    [Inject] protected ClientResourceWorkspace Resources { get; set; } = null!;
    [Inject] protected SkiaAssetRenderer Renderer { get; set; } = null!;
    protected bool busy;
    protected string? error, exported;
    protected string output = "";
    protected Task Run(Action action) => RunAsync(() => { action(); return Task.CompletedTask; });
    protected async Task RunAsync(Func<Task> action)
    {
        if (busy) return;
        busy = true; error = null; exported = null;
        try { await action(); }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException or UnauthorizedAccessException or OverflowException or KeyNotFoundException)
        { error = ex.Message; }
        finally { busy = false; }
    }
    protected static int Id(string input)
    {
        input = input.Trim();
        bool valid = input.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? int.TryParse(input[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int id)
            : int.TryParse(input, out id);
        return valid && id >= 0 ? id : throw new ArgumentException("Enter a nonnegative decimal or hexadecimal ID.");
    }
    protected static async Task<byte[]> Upload(IBrowserFile file)
    {
        await using var input = file.OpenReadStream(GumpImageImporter.MaxUploadBytes);
        using var buffer = new MemoryStream(); await input.CopyToAsync(buffer); return buffer.ToArray();
    }
    protected string Png(Fiddler.Formats.DecodedImage image) => "data:image/png;base64," + Convert.ToBase64String(Renderer.RenderPng(image));
}
