using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Fiddler.Formats;
using Fiddler.Formats.Gumps;
using FiddlerWebApp.Api;
using FiddlerWebApp.Services;
using FiddlerWebApp.Services.Users;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SkiaSharp;
using Xunit;

namespace Fiddler.Tests;

public sealed class ApiTests
{
    [Fact]
    public async Task AnonymousApiReturnsJsonWithoutRedirectAndUnknownRoutesAreJson()
    {
        using var host = new Host();
        using var client = host.Client(null);
        await Problem(await client.GetAsync("/api/v1/me"), 401);
        await Problem(await client.GetAsync("/api/openapi/v1.json"), 401);
        await Problem(await client.GetAsync("/api/v1/does-not-exist"), 404);
    }

    [Fact]
    public async Task WorkspacesBelongToBothUserAndLoginAndWritesNeedCsrfAndEtag()
    {
        using var host = new Host();
        using var alice = host.Client("alice"); using var bob = host.Client("bob");
        using var otherLogin = host.Client("alice", "another-login");
        await Problem(await alice.PostAsJsonAsync("/api/v1/workspaces", new { directory = host.Data }), 400);
        await Csrf(alice); var workspace = await Open(alice, host.Data);
        await Problem(await bob.GetAsync(workspace.Path), 404);
        await Problem(await otherLogin.GetAsync(workspace.Path), 404);
        Assert.Empty((await bob.GetFromJsonAsync<JsonElement[]>("/api/v1/workspaces"))!);
        await Problem(await Write(alice, HttpMethod.Post, workspace.Path + "/leases/gumps/1", null), 428);
        await Problem(await Write(alice, HttpMethod.Post, workspace.Path + "/leases/gumps/1", "\"0\""), 412);
        var reserve = await Write(alice, HttpMethod.Post, workspace.Path + "/leases/gumps/1", workspace.Tag);
        Assert.Equal(HttpStatusCode.OK, reserve.StatusCode);
        Assert.Equal("\"2\"", reserve.Headers.ETag!.Tag);
        // A separate HTTP request continues the same workspace and lease.
        Assert.Single((await alice.GetFromJsonAsync<JsonElement[]>(workspace.Path + "/leases"))!);
        Assert.Equal("\"1\"", workspace.Tag);
        await Problem(await Write(alice, HttpMethod.Delete, workspace.Path, workspace.Tag), 412);
        Assert.Equal(HttpStatusCode.NoContent, (await Write(alice, HttpMethod.Delete, workspace.Path, "\"2\"")).StatusCode);
        await Problem(await alice.GetAsync(workspace.Path), 404);
        Assert.Empty(host.Services.GetRequiredService<ResourceLeaseManager>().List());
    }

    [Fact]
    public async Task GumpDraftsArePrivateAndPublishingMergesDifferentUsersWithoutChangingOriginals()
    {
        using var host = new Host();
        byte[] original = File.ReadAllBytes(Path.Combine(host.Data, "gumpart.mul"));
        using var alice = host.Client("alice"); using var bob = host.Client("bob");
        await Csrf(alice); await Csrf(bob);
        var a = await Open(alice, host.Data); var b = await Open(bob, host.Data);
        Assert.Equal(HttpStatusCode.OK, (await Write(alice, HttpMethod.Post, a.Path + "/leases/gumps/1", a.Tag)).StatusCode);
        await Problem(await Write(bob, HttpMethod.Post, b.Path + "/leases/gumps/1", b.Tag), 409);
        Assert.Equal(SKColors.Red, await Pixel(bob, b.Path + "/gumps/1/image"));
        Assert.Equal(HttpStatusCode.OK, (await Write(alice, HttpMethod.Put, a.Path + "/gumps/1/image", "\"2\"", Png(SKColors.Lime))).StatusCode);
        Assert.Equal(SKColors.Lime, await Pixel(alice, a.Path + "/gumps/1/image"));
        Assert.Equal(SKColors.Red, await Pixel(bob, b.Path + "/gumps/1/image"));
        await Problem(await Write(alice, HttpMethod.Delete, a.Path, "\"3\""), 409);
        await Problem(await Write(alice, HttpMethod.Delete, a.Path + "/leases/gumps/1", "\"3\""), 409);
        Assert.Equal(HttpStatusCode.OK, (await Write(bob, HttpMethod.Post, b.Path + "/leases/gumps/2", b.Tag)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Write(bob, HttpMethod.Put, b.Path + "/gumps/2/image", "\"2\"", Png(SKColors.Blue))).StatusCode);
        var first = await Write(alice, HttpMethod.Post, a.Path + "/publish", "\"3\"", JsonContent.Create(new { outputDirectory = host.Output }));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var second = await Write(bob, HttpMethod.Post, b.Path + "/publish", "\"3\"", JsonContent.Create(new { outputDirectory = host.Output }));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var saved = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, saved.GetProperty("revision").GetInt64());
        using var document = GumpDocument.Open(saved.GetProperty("directory").GetString()!);
        Assert.Equal(SKColors.Lime, Color(document.Read(1)));
        Assert.Equal(SKColors.Blue, Color(document.Read(2)));
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(host.Data, "gumpart.mul")));
        Assert.Empty(host.Services.GetRequiredService<ResourceLeaseManager>().List());
        Assert.Equal(HttpStatusCode.OK, (await Write(alice, HttpMethod.Post, a.Path + "/refresh", "\"4\"")).StatusCode);
        Assert.Equal(SKColors.Blue, await Pixel(alice, a.Path + "/gumps/2/image"));
    }

    [Fact]
    public async Task InvalidUploadsKeepStateAndConcurrentWritesRejectStaleVersions()
    {
        using var host = new Host(); using var client = host.Client("alice"); await Csrf(client);
        var w = await Open(client, host.Data);
        await Problem(await Write(client, HttpMethod.Put, w.Path + "/gumps/1/image", w.Tag, Png(SKColors.Blue)), 409);
        var responses = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Write(client, HttpMethod.Post, w.Path + "/leases/gumps/1", w.Tag)));
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, r => (int)r.StatusCode == 412);
        await Problem(await Write(client, HttpMethod.Put, w.Path + "/gumps/1/image", "\"2\"", new StringContent("wrong type")), 415);
        var invalid = new ByteArrayContent([1, 2, 3]); invalid.Headers.ContentType = new("image/png");
        await Problem(await Write(client, HttpMethod.Put, w.Path + "/gumps/1/image", "\"2\"", invalid), 422);
        var large = new ByteArrayContent(new byte[8 * 1024 * 1024 + 1]); large.Headers.ContentType = new("image/png");
        await Problem(await Write(client, HttpMethod.Put, w.Path + "/gumps/1/image", "\"2\"", large), 413);
        var state = await client.GetAsync(w.Path);
        Assert.Equal("\"2\"", state.Headers.ETag!.Tag);
        Assert.Equal(0, (await state.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("draftCount").GetInt32());
        Assert.Equal(SKColors.Red, await Pixel(client, w.Path + "/gumps/1/image"));
    }

    [Fact]
    public async Task CompareCopyUndoAndPaginationWorkAcrossHttpRequests()
    {
        using var host = new Host(); using var client = host.Client("alice"); await Csrf(client);
        var w = await Open(client, host.Data);
        var list = await client.GetFromJsonAsync<JsonElement>(w.Path + "/gumps?offset=1&limit=1");
        Assert.Equal(2, list.GetProperty("total").GetInt32());
        Assert.Equal(2, list.GetProperty("items")[0].GetProperty("id").GetInt32());
        await Problem(await client.GetAsync(w.Path + "/gumps?limit=101"), 400);
        await Problem(await client.GetAsync(w.Path + "/gumps/999"), 404);
        var compare = await client.PostAsJsonAsync(w.Path + "/gumps/compare", new { referencePath = host.Reference, includeEqual = true });
        Assert.Equal(HttpStatusCode.OK, compare.StatusCode);
        Assert.Equal(1, (await compare.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("summary").GetProperty("Different").GetInt32());
        Assert.Equal(HttpStatusCode.OK, (await Write(client, HttpMethod.Post, w.Path + "/leases/gumps/1", w.Tag)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Write(client, HttpMethod.Post, w.Path + "/gumps/1/copy", "\"2\"", JsonContent.Create(new { referencePath = host.Reference, referenceId = 1 }))).StatusCode);
        Assert.Equal(SKColors.Blue, await Pixel(client, w.Path + "/gumps/1/image"));
        Assert.Equal(HttpStatusCode.NoContent, (await Write(client, HttpMethod.Delete, w.Path + "/gumps/1/draft", "\"3\"")).StatusCode);
        Assert.Equal(SKColors.Red, await Pixel(client, w.Path + "/gumps/1/image"));
        Assert.Empty(host.Services.GetRequiredService<ResourceLeaseManager>().List());
        var badJson = new StringContent("{", System.Text.Encoding.UTF8, "application/json");
        await Problem(await client.PostAsync("/api/v1/workspaces", badJson), 400);
    }

    [Fact]
    public async Task WorldAndAnimationEndpointsReturnBoundedMetadataAndPngs()
    {
        using var host = new Host(); using var client = host.Client("alice"); await Csrf(client);
        var w = await Open(client, host.Data);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(w.Path + "/files")).StatusCode);
        Assert.Equal(2, (await client.GetFromJsonAsync<JsonElement>(w.Path + "/multis?source=mul")).GetProperty("results").GetProperty("total").GetInt32());
        Assert.Equal(2, (await client.GetFromJsonAsync<JsonElement>(w.Path + "/multis/44?source=mul")).GetProperty("components").GetProperty("total").GetInt32());
        await Image(client, w.Path + "/multis/44/image?source=mul");
        await Image(client, w.Path + "/multis/44/image?source=uop");
        var region = await client.GetFromJsonAsync<JsonElement>(w.Path + "/maps/0/region?x=0&y=0&size=4&width=16&height=16&source=mul");
        Assert.Equal(16, region.GetProperty("land").GetArrayLength());
        await Image(client, w.Path + "/maps/0/region/image?x=0&y=0&size=4&width=16&height=16&source=mul");
        await Problem(await client.GetAsync(w.Path + "/maps/0/region?x=0&y=0&size=17&width=16&height=16"), 400);
        await Problem(await client.GetAsync(w.Path + "/maps/0/region?x=0&y=0&width=16"), 400);
        await Image(client, w.Path + "/art/items/1/image");
        var bodies = await client.GetFromJsonAsync<JsonElement>(w.Path + "/animations/1/bodies");
        Assert.Equal(1, bodies.GetProperty("total").GetInt32());
        var sequence = w.Path + "/animations/1/bodies/0/actions/0/directions/0";
        Assert.Equal(2, (await client.GetFromJsonAsync<JsonElement>(sequence)).GetProperty("frameCount").GetInt32());
        await Image(client, sequence + "/image");
    }

    [Fact]
    public async Task BrowserAndApiUseTheSameLeasesAndLogoutRevokesApiContexts()
    {
        using var host = new Host(); using var client = host.Client("alice"); await Csrf(client);
        var w = await Open(client, host.Data);
        using var browserScope = host.Services.CreateScope();
        var user = browserScope.ServiceProvider.GetRequiredService<UserSession>(); user.Initialize(Identity("bob", "browser"));
        var browser = browserScope.ServiceProvider.GetRequiredService<MulWorkspace>(); browser.Open(host.Data); browser.BeginEdit(1);
        await Problem(await Write(client, HttpMethod.Post, w.Path + "/leases/gumps/1", w.Tag), 409);
        browser.ReleaseEdit(1);
        Assert.Equal(HttpStatusCode.OK, (await Write(client, HttpMethod.Post, w.Path + "/leases/gumps/1", w.Tag)).StatusCode);
        host.Services.GetRequiredService<UserDirectory>().Revoke("alice-login", DateTimeOffset.UtcNow.AddHours(8));
        await Problem(await client.GetAsync(w.Path), 401);
        Assert.Empty(host.Services.GetRequiredService<ResourceLeaseManager>().List());
        host.Services.GetRequiredService<ApiWorkspaceRegistry>().Sweep();
        Assert.Empty(host.Services.GetRequiredService<ApiWorkspaceRegistry>().List(Identity("alice", "alice-login")));
    }

    [Fact]
    public async Task IdleContextsExpireButHeartbeatRenewsLiveLeases()
    {
        using var host = new Host(); using var client = host.Client("alice"); await Csrf(client);
        var w = await Open(client, host.Data);
        Assert.Equal(HttpStatusCode.OK, (await Write(client, HttpMethod.Post, w.Path + "/leases/gumps/1", w.Tag)).StatusCode);
        host.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync(w.Path + "/heartbeat", null)).StatusCode);
        host.Clock.Advance(TimeSpan.FromSeconds(90));
        Assert.Single(host.Services.GetRequiredService<ResourceLeaseManager>().List());
        host.Clock.Advance(TimeSpan.FromMinutes(16));
        await Problem(await client.GetAsync(w.Path), 404);
        Assert.Empty(host.Services.GetRequiredService<ResourceLeaseManager>().List());
        Assert.Empty((await client.GetFromJsonAsync<JsonElement[]>("/api/v1/workspaces"))!);
    }

    [Fact]
    public async Task OpenApiDocumentDescribesRoutesAndUploadContract()
    {
        using var host = new Host(); using var client = host.Client("alice");
        var document = await client.GetFromJsonAsync<JsonElement>("/api/openapi/v1.json");
        var paths = document.GetProperty("paths");
        Assert.True(paths.TryGetProperty("/api/v1/workspaces/{workspaceId}/publish", out _));
        var upload = paths.GetProperty("/api/v1/workspaces/{workspaceId}/gumps/{id}/image").GetProperty("put");
        Assert.True(upload.GetProperty("requestBody").GetProperty("content").TryGetProperty("image/png", out _));
        Assert.Contains(upload.GetProperty("parameters").EnumerateArray(), p => p.GetProperty("name").GetString() == "If-Match" && p.GetProperty("required").GetBoolean());
        Assert.True(document.GetProperty("components").GetProperty("securitySchemes").TryGetProperty("WorkspaceCookie", out _));
    }

    [Fact]
    public async Task RealLoginCookieWorksWithCsrfAndSignOutRevokesExistingApiWorkspaces()
    {
        using var host = new Host(nativeCookie: true); using var client = host.Client(null);
        await Problem(await client.GetAsync("/api/v1/me"), 401);
        var html = await client.GetStringAsync("/");
        var formToken = System.Text.RegularExpressions.Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(formToken.Success, html);
        var login = await client.PostAsync("/account/development-profile", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["name"] = "Cookie user", ["__RequestVerificationToken"] = WebUtility.HtmlDecode(formToken.Groups[1].Value) }));
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.Contains(login.Headers.GetValues("Set-Cookie"), value => value.StartsWith("Fiddler.User=", StringComparison.Ordinal));
        Assert.Equal("Cookie user", (await client.GetFromJsonAsync<JsonElement>("/api/v1/me")).GetProperty("name").GetString());
        await Csrf(client); var w = await Open(client, host.Data);
        Assert.Equal(HttpStatusCode.OK, (await Write(client, HttpMethod.Post, w.Path + "/leases/gumps/1", w.Tag)).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await client.PostAsync("/account/sign-out", null)).StatusCode);
        await Problem(await client.GetAsync(w.Path), 401);
        Assert.Empty(host.Services.GetRequiredService<ResourceLeaseManager>().List());
    }

    [Fact]
    public async Task OpenModeServesEveryClientAsTheSharedLocalUserWithoutSignIn()
    {
        using var host = new Host(openAccess: true); using var first = host.Client(null); using var second = host.Client(null);
        var me = await first.GetFromJsonAsync<JsonElement>("/api/v1/me");
        Assert.Equal("Local user", me.GetProperty("name").GetString());
        Assert.Equal("Local user", (await second.GetFromJsonAsync<JsonElement>("/api/v1/me")).GetProperty("name").GetString());
        Assert.DoesNotContain("Sign in to Fiddler", await first.GetStringAsync("/"));
        await Csrf(first); var w = await Open(first, host.Data);
        Assert.Equal(HttpStatusCode.OK, (await Write(first, HttpMethod.Post, w.Path + "/leases/gumps/1", w.Tag)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await first.PostAsync("/account/sign-out", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await first.GetAsync(w.Path)).StatusCode);
    }

    private static async Task Csrf(HttpClient client)
    {
        var token = await client.GetFromJsonAsync<JsonElement>("/api/v1/antiforgery");
        client.DefaultRequestHeaders.Add(token.GetProperty("headerName").GetString()!, token.GetProperty("token").GetString());
    }
    private static async Task<(string Path, string Tag)> Open(HttpClient client, string directory)
    {
        var response = await client.PostAsJsonAsync("/api/v1/workspaces", new { directory });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (response.Headers.Location!.ToString(), response.Headers.ETag!.Tag);
    }
    private static async Task<HttpResponseMessage> Write(HttpClient client, HttpMethod method, string path, string? tag, HttpContent? body = null)
    {
        using var request = new HttpRequestMessage(method, path) { Content = body };
        if (tag is not null) request.Headers.TryAddWithoutValidation("If-Match", tag);
        return await client.SendAsync(request);
    }
    private static async Task Problem(HttpResponseMessage response, int status)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True((int)response.StatusCode == status, $"Expected {status}, received {(int)response.StatusCode}: {text}");
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(status, JsonDocument.Parse(text).RootElement.GetProperty("status").GetInt32());
        Assert.Null(response.Headers.Location);
    }
    private static ByteArrayContent Png(SKColor color)
    {
        using var bitmap = new SKBitmap(1, 1); bitmap.Erase(color);
        using var image = SKImage.FromBitmap(bitmap); using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        var content = new ByteArrayContent(encoded.ToArray()); content.Headers.ContentType = new("image/png"); return content;
    }
    private static async Task Image(HttpClient client, string path)
    {
        var response = await client.GetAsync(path); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        using var bitmap = SKBitmap.Decode(await response.Content.ReadAsByteArrayAsync()); Assert.NotNull(bitmap);
    }
    private static async Task<SKColor> Pixel(HttpClient client, string path)
    { using var bitmap = SKBitmap.Decode(await client.GetByteArrayAsync(path)); return bitmap.GetPixel(0, 0); }
    private static SKColor Color(DecodedImage image) => new(unchecked((uint)image.Pixels.Span[0]));
    private static ClaimsPrincipal Identity(string user, string login) => new(new ClaimsIdentity([
        new(ClaimTypes.NameIdentifier, user), new(ClaimTypes.Name, user), new("fiddler:user", user), new("fiddler:login", login), new("fiddler:provider", "Test"),
        new("fiddler:expires", DateTimeOffset.UtcNow.AddHours(8).ToUnixTimeSeconds().ToString())], "ApiTest"));

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan time) => now += time;
    }
    private sealed class Host : WebApplicationFactory<Program>
    {
        private readonly bool nativeCookie;
        private readonly bool openAccess;
        private readonly string root = Path.Combine(Path.GetTempPath(), "fiddler-api-" + Guid.NewGuid());
        public string Data => Path.Combine(root, "client");
        public string Output => Path.Combine(root, "exports");
        public string Reference => Path.Combine(root, "reference");
        public Clock Clock { get; } = new();
        public Host(bool nativeCookie = false, bool openAccess = false)
        {
            this.nativeCookie = nativeCookie;
            this.openAccess = openAccess;
            Directory.CreateDirectory(Data); Directory.CreateDirectory(Reference);
            WorldAssetTests.WriteSceneFixtures(Data);
            GumpRecord Solid(int color) => GumpEncoder.Encode(DecodedImage.FromArgb(1, 1, [color]));
            GumpEditingTests.WriteMul(Data, new() { [1] = Solid(unchecked((int)0xFFFF0000)), [2] = Solid(unchecked((int)0xFFFF0000)) });
            GumpEditingTests.WriteMul(Reference, new() { [1] = Solid(unchecked((int)0xFF0000FF)), [2] = Solid(unchecked((int)0xFFFF0000)) });
            byte[] animation = AnimationTests.Fixture(); File.WriteAllBytes(Path.Combine(Data, "anim.mul"), animation);
            using var writer = new BinaryWriter(File.Create(Path.Combine(Data, "anim.idx")));
            writer.Write(0); writer.Write(animation.Length); writer.Write(0);
        }
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            if (openAccess) builder.UseSetting("Authentication:Mode", "Open");
            builder.UseContentRoot(WebRoot());
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(Clock);
                services.RemoveAll<SharedGumpStore>();
                services.AddSingleton(s => new SharedGumpStore(Path.Combine(root, "shared"), s.GetRequiredService<ResourceLeaseManager>()));
                services.RemoveAll<ClientDirectorySettings>();
                services.AddScoped(s => new ClientDirectorySettings(Path.Combine(root, "settings", s.GetRequiredService<UserSession>().RequireUser().Id + ".json"), Data));
                if (nativeCookie)
                {
                    services.RemoveAll<SignInFeatures>(); services.AddSingleton(new SignInFeatures(false, true));
                }
                else if (!openAccess) services.AddAuthentication(options => { options.DefaultAuthenticateScheme = "ApiTest"; options.DefaultChallengeScheme = "ApiTest"; })
                    .AddScheme<AuthenticationSchemeOptions, TestAuthentication>("ApiTest", _ => { });
                services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));
            });
        }
        public HttpClient Client(string? user, string? login = null)
        {
            var client = CreateClient(new() { AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") });
            if (user is not null) { client.DefaultRequestHeaders.Add("X-Test-User", user); client.DefaultRequestHeaders.Add("X-Test-Login", login ?? user + "-login"); }
            return client;
        }
        public override async ValueTask DisposeAsync()
        { await base.DisposeAsync(); if (Directory.Exists(root)) Directory.Delete(root, true); }
        private static string WebRoot([System.Runtime.CompilerServices.CallerFilePath] string sourceFile = "") =>
            Path.GetFullPath("../../src/FiddlerWebApp", Path.GetDirectoryName(sourceFile)!);
    }
    // This handler exists only in the test assembly; production accepts no identity headers.
    private sealed class TestAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(
            Request.Headers.TryGetValue("X-Test-User", out var user)
            ? AuthenticateResult.Success(new AuthenticationTicket(Identity(user.ToString(), Request.Headers["X-Test-Login"].ToString()), Scheme.Name))
            : AuthenticateResult.NoResult());
        protected override Task HandleChallengeAsync(AuthenticationProperties properties) =>
            Microsoft.AspNetCore.Http.Results.Problem(statusCode: 401, detail: "Sign in before using the API.").ExecuteAsync(Context);
    }
}
