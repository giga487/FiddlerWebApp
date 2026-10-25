using UOAS.Application.Collaboration;

namespace UOAS.Backend.Endpoints;

public sealed class CollaborationGuard(CollaborationSessions sessions, ILogger<CollaborationGuard> logger) : IEndpointFilter
{
    public static CollaborationSessions.Session Session(HttpContext http) => (CollaborationSessions.Session)http.Items[typeof(CollaborationSessions.Session)]!;
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        http.Response.Headers.CacheControl = "no-store";
        string token = http.Request.Headers["X-UOAS-Session"].ToString();
        if (token.Length != 64) return Results.Problem(statusCode: 400, detail: "A valid collaboration session is required. Enter your name to start a session.");
        try
        {
            using var use = await sessions.AcquireAsync(token, http.RequestAborted);
            http.Items[typeof(CollaborationSessions.Session)] = use.Session;
            return await next(context);
        }
        catch (KeyNotFoundException ex) { return Results.Problem(statusCode: 404, detail: ex.Message); }
        catch (FileNotFoundException ex) { return Results.Problem(statusCode: 404, detail: ex.Message); }
        catch (DirectoryNotFoundException ex) { return Results.Problem(statusCode: 404, detail: ex.Message); }
        catch (ArgumentException ex) { return Results.Problem(statusCode: 400, detail: ex.Message); }
        catch (System.Text.Json.JsonException ex) { return Results.Problem(statusCode: 422, detail: ex.Message); }
        catch (FormatException ex) { return Results.Problem(statusCode: 422, detail: ex.Message); }
        catch (InvalidDataException ex) { return Results.Problem(statusCode: 422, detail: ex.Message); }
        catch (InvalidOperationException ex) { return Results.Problem(statusCode: 409, detail: ex.Message); }
        catch (UnauthorizedAccessException) { return Results.Problem(statusCode: 403, detail: "The backend cannot access this resource."); }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested) { return Results.StatusCode(499); }
        catch (IOException ex) { logger.LogError(ex, "Asset operation failed"); return Results.Problem(statusCode: 500, detail: "File operation failed. Drafts remain available."); }
    }
}
