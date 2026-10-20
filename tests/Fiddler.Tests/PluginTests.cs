using System.IO.Compression;
using System.Security.Claims;
using Fiddler.Plugin.Abstractions;
using Fiddler.Plugin.AssetInfo;
using FiddlerWebApp.Services;
using FiddlerWebApp.Services.Plugins;
using FiddlerWebApp.Services.Users;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Xunit;

namespace Fiddler.Tests;

public sealed class PluginTests
{
    [Fact]
    public async Task DllInstallationActivationExecutionAndDisableSurviveRestart()
    {
        using var fixture = new Fixture();
        using var admin = fixture.User("admin");
        string key;
        using (var registry = fixture.Registry())
        {
            await registry.StartAsync(default);
            await using var dll = File.OpenRead(typeof(AssetInfoPlugin).Assembly.Location);
            await registry.InstallAsync(admin, "AssetInfo.dll", "", dll);
            var installed = Assert.Single(await registry.ListAsync(admin));
            key = installed.Key;
            Assert.False(installed.Loaded);
            Assert.False(installed.Enabled);
            await Assert.ThrowsAsync<InvalidOperationException>(() => registry.ExecuteAsync(admin, key, "inspect", Image()));
            await registry.SetEnabledAsync(admin, key, true);
            var active = Assert.Single(await registry.ListAsync(admin));
            Assert.Null(active.Error);
            Assert.True(active.Loaded);
            Assert.Equal("fiddler.asset-info", active.Id);
            Assert.Equal("inspect", Assert.Single(active.Commands).Id);
            string text = await registry.ExecuteAsync(admin, key, "inspect", Image());
            Assert.Contains("Visible pixels: 2", text);
            Assert.Contains("Transparent pixels: 1", text);
            Assert.Contains("Distinct visible RGB colors: 1", text);
            await Assert.ThrowsAsync<ArgumentException>(() => registry.ExecuteAsync(admin, key, "missing", Image()));
            await Assert.ThrowsAsync<InvalidOperationException>(() => registry.RemoveAsync(admin, key));
        }
        using (var restarted = fixture.Registry())
        {
            await restarted.StartAsync(default);
            Assert.True(Assert.Single(await restarted.ListAsync(admin)).Loaded);
            await restarted.SetEnabledAsync(admin, key, false);
            await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.ExecuteAsync(admin, key, "inspect", Image()));
        }
        using (var restarted = fixture.Registry())
        {
            await restarted.StartAsync(default);
            Assert.False(Assert.Single(await restarted.ListAsync(admin)).Enabled);
            await restarted.RemoveAsync(admin, key);
            Assert.Empty(await restarted.ListAsync(admin));
        }
    }

    [Fact]
    public async Task NonAdministratorsAndRevokedSessionsCannotManageOrExecutePlugins()
    {
        using var fixture = new Fixture();
        using var registry = fixture.Registry();
        await registry.StartAsync(default);
        using var user = fixture.User("reader");
        Assert.False(registry.CanManage(user));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => registry.ListAsync(user));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => registry.InstallAsync(user, "plugin.dll", "", Stream.Null));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => registry.SetEnabledAsync(user, "missing", true));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => registry.RemoveAsync(user, "missing"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => registry.ExecuteAsync(user, "missing", "inspect", Image()));
        using var admin = fixture.User("admin");
        Assert.True(registry.CanManage(admin));
        fixture.Users.Revoke(admin.LoginId, DateTimeOffset.UtcNow.AddHours(1));
        Assert.False(registry.CanManage(admin));
        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.ListAsync(admin));
    }

    [Fact]
    public async Task ZipResolvesSharedContractAndRejectsDuplicateActivePluginIds()
    {
        using var fixture = new Fixture();
        using var registry = fixture.Registry();
        using var admin = fixture.User("admin");
        await registry.StartAsync(default);
        using var zip = Package(("nested/AssetInfo.dll", File.ReadAllBytes(typeof(AssetInfoPlugin).Assembly.Location)),
            ("nested/Fiddler.Plugin.Abstractions.dll", File.ReadAllBytes(typeof(IFiddlerPlugin).Assembly.Location)));
        await registry.InstallAsync(admin, "plugin.zip", "nested/AssetInfo.dll", zip);
        var first = Assert.Single(await registry.ListAsync(admin));
        await registry.SetEnabledAsync(admin, first.Key, true);
        Assert.True(Assert.Single(await registry.ListAsync(admin)).Loaded);
        await using var dll = File.OpenRead(typeof(AssetInfoPlugin).Assembly.Location);
        await registry.InstallAsync(admin, "duplicate.dll", "", dll);
        var second = (await registry.ListAsync(admin)).Single(item => item.Key != first.Key);
        await registry.SetEnabledAsync(admin, second.Key, true);
        var views = await registry.ListAsync(admin);
        Assert.True(views.Single(item => item.Key == first.Key).Loaded);
        Assert.False(views.Single(item => item.Key == second.Key).Loaded);
        Assert.Contains("already active", views.Single(item => item.Key == second.Key).Error);
    }

    [Theory]
    [InlineData("../escape.dll")]
    [InlineData("/escape.dll")]
    [InlineData("nested/../../escape.dll")]
    [InlineData("C:/escape.dll")]
    [InlineData("nested\\escape.dll")]
    [InlineData(".. /escape.dll")]
    [InlineData("folder./escape.dll")]
    public async Task ZipCannotWriteOutsideItsInstallation(string path)
    {
        using var fixture = new Fixture();
        using var registry = fixture.Registry();
        using var admin = fixture.User("admin");
        await registry.StartAsync(default);
        using var zip = Package((path, new byte[] { 1, 2, 3 }));
        await Assert.ThrowsAsync<InvalidDataException>(() => registry.InstallAsync(admin, "bad.zip", "plugin.dll", zip));
        Assert.Empty(await registry.ListAsync(admin));
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Root));
    }

    [Fact]
    public async Task InvalidDllIsCleanedUpAndAnAssemblyWithoutAPluginCannotActivate()
    {
        using var fixture = new Fixture();
        using var registry = fixture.Registry();
        using var admin = fixture.User("admin");
        await registry.StartAsync(default);
        using var invalid = new MemoryStream([1, 2, 3]);
        await Assert.ThrowsAsync<BadImageFormatException>(() => registry.InstallAsync(admin, "bad.dll", "", invalid));
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Root));
        await using var contract = File.OpenRead(typeof(IFiddlerPlugin).Assembly.Location);
        await registry.InstallAsync(admin, "contract.dll", "", contract);
        var installed = Assert.Single(await registry.ListAsync(admin));
        await registry.SetEnabledAsync(admin, installed.Key, true);
        var failed = Assert.Single(await registry.ListAsync(admin));
        Assert.False(failed.Loaded);
        Assert.NotNull(failed.Error);
    }

    [Theory]
    [InlineData("admin", true)]
    [InlineData("reader", false)]
    public async Task ManagementPageShowsUploadControlsOnlyToAdministrators(string id, bool administrator)
    {
        using var fixture = new Fixture();
        using var registry = fixture.Registry();
        using var user = fixture.User(id);
        await registry.StartAsync(default);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(registry);
        services.AddSingleton(user);
        services.AddSingleton<MulWorkspace>();
        services.AddSingleton(new ClientDirectorySettings(Path.Combine(fixture.Root, "settings.json"), ""));
        services.AddSingleton<IJSRuntime, NoJavaScript>();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        string html = await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<FiddlerWebApp.Components.Pages.Plugins>()).ToHtmlString());
        Assert.Equal(administrator, html.Contains("type=\"file\""));
        Assert.Equal(administrator, html.Contains("Install inactive"));
        if (!administrator) Assert.Contains("Your account ID", html);
    }

    private sealed class NoJavaScript : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => new(default(TValue)!);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => new(default(TValue)!);
    }

    private static PluginAsset Image() => new("Item", 42, 3, 1, [0, unchecked((int)0xFFFF0000), unchecked((int)0xFFFF0000)]);
    private static MemoryStream Package(params (string Path, byte[] Data)[] files)
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
            foreach (var file in files)
            {
                using var output = zip.CreateEntry(file.Path).Open();
                output.Write(file.Data);
            }
        stream.Position = 0;
        return stream;
    }
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "fiddler-plugins-" + Guid.NewGuid().ToString("N"));
        public UserDirectory Users { get; } = new(TimeProvider.System);
        public PluginRegistry Registry() => new(Root, new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Plugins:AdministratorIds:0"] = "admin" }).Build(), NullLogger<PluginRegistry>.Instance);
        public UserSession User(string id)
        {
            var session = new UserSession(Users, new ResourceLeaseManager(TimeProvider.System), TimeProvider.System);
            session.Initialize(new ClaimsPrincipal(new ClaimsIdentity([
                new(ClaimTypes.Name, id), new("fiddler:user", id), new("fiddler:login", Guid.NewGuid().ToString("N")),
                new("fiddler:expires", DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds().ToString())], "Test")));
            return session;
        }
        public void Dispose()
        {
            // Collectible contexts can retain Windows file handles until collection.
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }
}
