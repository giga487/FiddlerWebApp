using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace FiddlerWebApp.Api;

public static class ApiDescription
{
    public static void Configure(OpenApiOptions options)
    {
        options.ShouldInclude = description => description.RelativePath?.StartsWith("api/v1/", StringComparison.Ordinal) == true;
        options.AddDocumentTransformer((document, _, _) =>
        {
            document.Info = new() { Title = "Fiddler HTTP API", Version = "v1", Description = "User-owned asset workspaces. Uses the web application's login cookie. MCP, bearer tokens and M2M are not implemented." };
            document.Components ??= new();
            document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
            document.Components.SecuritySchemes["WorkspaceCookie"] = new OpenApiSecurityScheme
            { Type = SecuritySchemeType.ApiKey, In = ParameterLocation.Cookie, Name = "Fiddler.User", Description = "Sign in through the web application first." };
            document.Security = [new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("WorkspaceCookie", document)] = [] }];
            return Task.CompletedTask;
        });
        options.AddOperationTransformer((operation, context, _) =>
        {
            bool read = context.Description.HttpMethod is "GET" or "HEAD";
            if (!read)
            {
                operation.Parameters ??= [];
                operation.Parameters.Add(Header("X-CSRF-TOKEN", "Use the token returned by GET /api/v1/antiforgery with the same login cookie."));
                string path = context.Description.RelativePath ?? "";
                if (path.Contains("{workspaceId", StringComparison.Ordinal) && !path.EndsWith("/heartbeat", StringComparison.Ordinal) && !path.EndsWith("/gumps/compare", StringComparison.Ordinal))
                    operation.Parameters.Add(Header("If-Match", "Exact workspace ETag, including quotes. Read the new ETag after every successful mutation."));
            }
            if (operation.RequestBody?.Content.TryGetValue("image/png", out var upload) == true)
                upload.Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "binary" };
            operation.Responses ??= new();
            operation.Responses.TryAdd("401", new OpenApiResponse { Description = "Sign-in required or login expired/revoked. JSON Problem Details; no redirect." });
            operation.Responses.TryAdd("400", new OpenApiResponse { Description = "Invalid parameters, JSON or antiforgery token." });
            return Task.CompletedTask;
        });
    }
    private static OpenApiParameter Header(string name, string description) => new()
    { Name = name, In = ParameterLocation.Header, Required = true, Description = description, Schema = new OpenApiSchema { Type = JsonSchemaType.String } };
}
