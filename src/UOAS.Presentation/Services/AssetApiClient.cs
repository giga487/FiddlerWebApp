using System.Net.Http.Json;
using UOAS.Contracts;

namespace UOAS.Presentation.Services;

public interface IAssetApi
{
    CollaborationSession? Session { get; }
    Task StartSessionAsync(string name, CancellationToken token = default);
    Task EndSessionAsync(CancellationToken token = default);
    Task<T> GetAsync<T>(string path, CancellationToken token = default);
    Task<T> PostAsync<T>(string path, object? body = null, CancellationToken token = default);
    Task PostAsync(string path, CancellationToken token = default);
    Task DeleteAsync(string path, CancellationToken token = default);
    Task<byte[]> ImageAsync(string path, CancellationToken token = default);
    Task UploadAsync(string path, byte[] png, CancellationToken token = default);
    Task PutAsync(string path, object value, CancellationToken token = default);
}

public sealed class AssetApiClient(HttpClient http) : IAssetApi
{
    public CollaborationSession? Session { get; private set; }
    public async Task StartSessionAsync(string name, CancellationToken token = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/sessions") { Content = JsonContent.Create(new StartSessionRequest(name)) };
        using var response = await http.SendAsync(request, token);
        await Ensure(response, token);
        Session = await response.Content.ReadFromJsonAsync<CollaborationSession>(token) ?? throw new InvalidDataException("The backend returned an empty session.");
    }
    public async Task EndSessionAsync(CancellationToken token = default)
    {
        try { if (Session is not null) await DeleteAsync("api/session", token); }
        finally { Session = null; }
    }
    private HttpRequestMessage Request(HttpMethod method, string path, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Add("X-UOAS-Session", Session?.Token ?? throw new InvalidOperationException("Enter your name to start a session."));
        return request;
    }
    private static async Task Ensure(HttpResponseMessage response, CancellationToken token)
    {
        if (response.IsSuccessStatusCode) return;
        string? detail = null;
        try { detail = (await response.Content.ReadFromJsonAsync<ApiProblem>(token))?.Detail; }
        catch (System.Text.Json.JsonException) { }
        throw new HttpRequestException(detail ?? $"The backend returned {(int)response.StatusCode}.", null, response.StatusCode);
    }
    private async Task<T> Json<T>(HttpMethod method, string path, object? body, CancellationToken token)
    {
        using var request = Request(method, path, body is null ? null : JsonContent.Create(body));
        using var response = await http.SendAsync(request, token);
        await Ensure(response, token);
        return await response.Content.ReadFromJsonAsync<T>(token) ?? throw new InvalidDataException("The backend returned an empty result.");
    }
    public Task<T> GetAsync<T>(string path, CancellationToken token = default) => Json<T>(HttpMethod.Get, path, null, token);
    public Task<T> PostAsync<T>(string path, object? body = null, CancellationToken token = default) => Json<T>(HttpMethod.Post, path, body, token);
    private async Task Send(HttpMethod method, string path, HttpContent? content, CancellationToken token)
    {
        using var request = Request(method, path, content);
        using var response = await http.SendAsync(request, token);
        await Ensure(response, token);
    }
    public Task PostAsync(string path, CancellationToken token = default) => Send(HttpMethod.Post, path, null, token);
    public Task DeleteAsync(string path, CancellationToken token = default) => Send(HttpMethod.Delete, path, null, token);
    public Task PutAsync(string path, object value, CancellationToken token = default) => Send(HttpMethod.Put, path, JsonContent.Create(value), token);
    public async Task<byte[]> ImageAsync(string path, CancellationToken token = default)
    {
        using var request = Request(HttpMethod.Get, path);
        using var response = await http.SendAsync(request, token);
        await Ensure(response, token);
        return await response.Content.ReadAsByteArrayAsync(token);
    }
    public Task UploadAsync(string path, byte[] png, CancellationToken token = default)
    {
        var content = new ByteArrayContent(png); content.Headers.ContentType = new("image/png");
        return Send(HttpMethod.Put, path, content, token);
    }
}
