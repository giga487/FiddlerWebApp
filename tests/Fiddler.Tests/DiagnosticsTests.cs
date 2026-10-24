using System.Security.Claims;
using System.Text.Json;
using FiddlerWebApp.Services.Diagnostics;
using FiddlerWebApp.Services.Users;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using Xunit;

namespace Fiddler.Tests;

public sealed class DiagnosticsTests
{
    [Fact]
    public void SinkFiltersStructuredEventsExceptionsTimeAndClearCursor()
    {
        var store = new DiagnosticLogStore(10);
        var start = DateTimeOffset.UtcNow;
        var template = new Serilog.Parsing.MessageTemplateParser().Parse("Asset {Id}");
        store.Emit(new(start, LogEventLevel.Information, null, template,
            [new("Id", new ScalarValue(42)), new("SourceContext", new ScalarValue("Assets.Gumps"))]));
        store.Emit(new(start.AddSeconds(1), LogEventLevel.Error, new IOException("Missing archive"), template,
            [new("Id", new ScalarValue(43)), new("SourceContext", new ScalarValue("Assets.Items"))]));
        var first = Assert.Single(store.Query(new(Category: "gUmPs", Message: "42")));
        Assert.Equal("Asset 42", first.Message);
        Assert.Equal("Asset {Id}", first.MessageTemplate);
        Assert.Contains("Id: 42", first.Properties);
        Assert.Single(store.Query(new(MinimumLevel: LogEventLevel.Warning, ExceptionsOnly: true, Message: "missing")));
        Assert.Single(store.Query(new(From: start.AddSeconds(1), Until: start.AddSeconds(2))));
        Assert.Empty(store.Query(new(From: start.AddSeconds(2))));
        Assert.Equal(2, store.Query(new())[0].Sequence);
        Assert.Empty(store.Query(new(AfterSequence: store.LatestSequence)));
    }

    [Fact]
    public void ConcurrentLoggingKeepsOnlyNewestBoundedEventsAndClipsLargeMessages()
    {
        var store = new DiagnosticLogStore(25);
        using var logger = new LoggerConfiguration().WriteTo.Sink(store).CreateLogger();
        Parallel.For(0, 200, id => logger.Information("Event {Id}", id));
        var entries = store.Query(new());
        Assert.Equal(25, entries.Count);
        Assert.Equal(Enumerable.Range(176, 25).Reverse().Select(id => (long)id), entries.Select(entry => entry.Sequence));
        logger.Information("{Message}", new string('x', 20000));
        Assert.Contains("[truncated]", store.Query(new())[0].Message);
        Assert.Equal(25, store.Query(new()).Count);
    }

    [Fact]
    public async Task MicrosoftLoggerReachesPageSinkAndRollingJsonFileWithScopesAndRuntimeVerbosity()
    {
        string root = Path.Combine(Path.GetTempPath(), "fiddler-diagnostics-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = root });
            builder.Configuration["Serilog:MinimumLevel:Default"] = "Information";
            builder.Configuration["Serilog:MinimumLevel:Override:Microsoft.AspNetCore"] = "Warning";
            builder.AddDiagnosticsLogging();
            await using (var app = builder.Build())
            {
                var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("DiagnosticsTest");
                var logs = app.Services.GetRequiredService<DiagnosticLogStore>();
                logger.LogDebug("Hidden debug event");
                Assert.Empty(logs.Query(new(Category: "DiagnosticsTest")));
                using (logger.BeginScope(new Dictionary<string, object> { ["OperationId"] = "operation-42" }))
                    logger.LogError(new IOException("Synthetic failure"), "Cannot open asset {AssetId}", 42);
                var entry = Assert.Single(logs.Query(new(Category: "DiagnosticsTest")));
                Assert.Contains("OperationId", entry.Properties);
                Assert.Contains("Synthetic failure", entry.Exception);
                app.Services.GetRequiredService<DiagnosticLogControl>().LevelSwitch.MinimumLevel = LogEventLevel.Verbose;
                logger.LogTrace("Trace enabled");
                Assert.Equal(2, logs.Query(new(Category: "DiagnosticsTest")).Count);
                app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Microsoft.AspNetCore.Test").LogInformation("Framework override hidden");
                Assert.Empty(logs.Query(new(Category: "Microsoft.AspNetCore.Test")));
            }
            string file = Assert.Single(Directory.GetFiles(Path.Combine(root, "App_Data", "logs"), "uoas-*.jsonl"));
            var json = File.ReadLines(file).Select(line => JsonDocument.Parse(line)).ToArray();
            try
            {
                var error = Assert.Single(json, document => document.RootElement.GetProperty("Level").GetString() == "Error");
                Assert.Contains("Cannot open asset 42", error.RootElement.GetProperty("RenderedMessage").GetString());
                Assert.Equal(42, error.RootElement.GetProperty("Properties").GetProperty("AssetId").GetInt32());
                Assert.Contains("Synthetic failure", error.RootElement.GetProperty("Exception").GetString());
            }
            finally { foreach (var document in json) document.Dispose(); }
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("admin", "Required", true)]
    [InlineData("reader", "Required", false)]
    [InlineData("open:local", "Open", true)]
    public async Task PageRestrictsLogAccessAndHtmlEncodesMessages(string id, string mode, bool allowed)
    {
        var users = new UserDirectory(TimeProvider.System);
        using var session = new UserSession(users, new ResourceLeaseManager(TimeProvider.System), TimeProvider.System);
        session.Initialize(new ClaimsPrincipal(new ClaimsIdentity([
            new("fiddler:user", id), new("fiddler:login", "login"),
            new("fiddler:expires", DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds().ToString())], "Test")));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Authentication:Mode"] = mode, ["Diagnostics:AdministratorIds:0"] = "admin" }).Build();
        var access = new DiagnosticsAccess(configuration);
        Assert.Equal(allowed, access.CanView(session));
        var store = new DiagnosticLogStore();
        using var logger = new LoggerConfiguration().WriteTo.Sink(store).CreateLogger();
        logger.Error("Sensitive event <script>alert(1)</script>");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(store); services.AddSingleton(access); services.AddSingleton(session);
        services.AddSingleton(new DiagnosticLogControl(LogEventLevel.Information));
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        string html = await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<FiddlerWebApp.Components.Pages.Diagnostics>()).ToHtmlString());
        Assert.Equal(allowed, html.Contains("Sensitive event"));
        Assert.Equal(allowed, html.Contains("Capture verbosity"));
        Assert.DoesNotContain("<script>", html);
        users.Revoke(session.LoginId, DateTimeOffset.UtcNow.AddHours(1));
        Assert.False(access.CanView(session));
    }
}
