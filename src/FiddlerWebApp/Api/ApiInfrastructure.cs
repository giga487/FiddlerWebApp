using FiddlerWebApp.Services.Users;
using Microsoft.AspNetCore.Antiforgery;

namespace FiddlerWebApp.Api;

public sealed class ApiException(int status, string message) : Exception(message)
{ public int Status { get; } = status; }

public sealed class ApiGuard(IAntiforgery antiforgery, ILogger<ApiGuard> logger) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        http.Response.Headers.CacheControl = "no-store";
        try
        {
            var user = http.RequestServices.GetRequiredService<UserSession>();
            user.Initialize(http.User);
            if (!user.IsSignedIn) throw new ApiException(401, "Sign in before using the API.");
            if (!HttpMethods.IsGet(http.Request.Method) && !HttpMethods.IsHead(http.Request.Method)) await antiforgery.ValidateRequestAsync(http);
            return await next(context);
        }
        catch (AntiforgeryValidationException) { return Problem(http, 400, "Invalid or missing antiforgery token. Request /api/v1/antiforgery first."); }
        catch (ApiException ex) { return Problem(http, ex.Status, ex.Message); }
        catch (BadHttpRequestException ex) { return Problem(http, ex.StatusCode, ex.Message); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or KeyNotFoundException) { return Problem(http, 404, ex.Message); }
        catch (InvalidDataException ex) { return Problem(http, 422, ex.Message); }
        catch (ArgumentException ex) { return Problem(http, 400, ex.Message); }
        catch (InvalidOperationException ex) { return Problem(http, 409, ex.Message); }
        catch (UnauthorizedAccessException) { return Problem(http, 403, "The server cannot access this resource."); }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested) { return Results.StatusCode(499); }
        catch (IOException ex) { logger.LogError(ex, "API file operation failed for {Path}", http.Request.Path); return Problem(http, 500, "The file operation failed. Drafts and the last published version are preserved."); }
    }
    public static IResult Problem(HttpContext http, int status, string detail) => Results.Problem(statusCode: status, detail: detail,
        extensions: new Dictionary<string, object?> { ["traceId"] = http.TraceIdentifier });
}

public static class ApiOperations
{
    public static async Task<IResult> Run(HttpContext http, ApiWorkspaceRegistry registry, Guid id, bool write, Func<ApiWorkspace, Task<IResult>> action)
    {
        await using var use = await registry.AcquireAsync(id, http.User, http.RequestAborted);
        var workspace = use.Workspace;
        if (write)
        {
            if (!http.Request.Headers.TryGetValue("If-Match", out var expected)) throw new ApiException(428, "If-Match is required. Read the workspace ETag before modifying it.");
            if (expected.ToString() != workspace.ETag) throw new ApiException(412, "The workspace changed. Read its latest ETag and review the current state before retrying.");
        }
        var result = await action(workspace);
        if (write) workspace.Version++;
        http.Response.Headers.ETag = workspace.ETag;
        return result;
    }
    public static Task<IResult> Read(HttpContext http, ApiWorkspaceRegistry registry, Guid id, Func<ApiWorkspace, IResult> action) =>
        Run(http, registry, id, false, w => Task.FromResult(action(w)));
    public static Task<IResult> Write(HttpContext http, ApiWorkspaceRegistry registry, Guid id, Func<ApiWorkspace, IResult> action) =>
        Run(http, registry, id, true, w => Task.FromResult(action(w)));
    public static (int Offset, int Limit) Page(int? offset, int? limit)
    {
        int start = offset ?? 0, size = limit ?? 24;
        if (start < 0 || size < 1 || size > 100) throw new ArgumentException("Offset must be nonnegative and limit must be between 1 and 100.");
        return (start, size);
    }
    public static bool? Source(string? source) => source?.ToLowerInvariant() switch
    { null or "auto" => null, "mul" => false, "uop" => true, _ => throw new ArgumentException("Source must be auto, mul or uop.") };
}
