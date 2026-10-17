using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Extensions.Options;

namespace FiddlerWebApp.Services.Users;

/// <summary>Required: users sign in (Google or development profiles). Open: everyone is the shared local user.</summary>
public enum WorkspaceAuthenticationMode { Required, Open }

public sealed record SignInFeatures(bool Google, bool DevelopmentProfiles, bool OpenAccess = false);

public static class WorkspaceAuthentication
{
    public static void AddWorkspaceAuthentication(this WebApplicationBuilder builder)
    {
        var mode = builder.Configuration.GetValue("Authentication:Mode", WorkspaceAuthenticationMode.Required);
        if (mode == WorkspaceAuthenticationMode.Open)
        {
            builder.Services.AddSingleton(new SignInFeatures(false, false, OpenAccess: true));
            builder.Services.AddCascadingAuthenticationState();
            builder.Services.AddAuthorization();
            builder.Services.AddAuthentication(OpenAccessAuthenticationHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, OpenAccessAuthenticationHandler>(OpenAccessAuthenticationHandler.SchemeName, _ => { });
            return;
        }
        string? clientId = builder.Configuration["Authentication:Google:ClientId"];
        string? secret = builder.Configuration["Authentication:Google:ClientSecret"];
        bool google = !string.IsNullOrWhiteSpace(clientId) && !string.IsNullOrWhiteSpace(secret);
        bool development = builder.Configuration.GetValue<bool>("Authentication:DevelopmentProfiles");
        if (development && !builder.Environment.IsDevelopment()) throw new InvalidOperationException("Development profiles can only be enabled in the Development environment.");
        builder.Services.AddSingleton(new SignInFeatures(google, development));
        builder.Services.AddCascadingAuthenticationState();
        builder.Services.AddAuthorization();
        var authentication = builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.Cookie.Name = "Fiddler.User";
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.ExpireTimeSpan = TimeSpan.FromHours(8);
                options.SlidingExpiration = false;
                options.LoginPath = "/";
                options.Events.OnRedirectToLogin = context =>
                {
                    if (context.Request.Path.StartsWithSegments("/api"))
                        return Results.Problem(statusCode: 401, detail: "Sign in before using the API.").ExecuteAsync(context.HttpContext);
                    context.Response.Redirect(context.RedirectUri); return Task.CompletedTask;
                };
                options.Events.OnRedirectToAccessDenied = context =>
                {
                    if (context.Request.Path.StartsWithSegments("/api"))
                        return Results.Problem(statusCode: 403, detail: "Access denied.").ExecuteAsync(context.HttpContext);
                    context.Response.Redirect(context.RedirectUri); return Task.CompletedTask;
                };
                options.Events.OnValidatePrincipal = context =>
                {
                    string? loginId = context.Principal?.FindFirstValue("fiddler:login");
                    if (loginId is null || context.HttpContext.RequestServices.GetRequiredService<UserDirectory>().IsRevoked(loginId)) context.RejectPrincipal();
                    return Task.CompletedTask;
                };
            });
        if (google) authentication.AddGoogle(options =>
        {
            options.ClientId = clientId!; options.ClientSecret = secret!;
            options.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            options.SaveTokens = false; options.UsePkce = true;
            options.Events.OnCreatingTicket = context =>
            {
                string id = context.Identity?.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? throw new InvalidOperationException("Google did not return an account identifier.");
                string? email = context.Identity?.FindFirst(ClaimTypes.Email)?.Value;
                string[] allowed = builder.Configuration.GetSection("Authentication:AllowedEmails").Get<string[]>() ?? [];
                bool verified = context.User.TryGetProperty("verified_email", out var verification) && verification.ValueKind == System.Text.Json.JsonValueKind.True;
                if (allowed.Length > 0 && (!verified || !allowed.Contains(email, StringComparer.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("This Google account is not allowed to use this workspace.");
                AddIdentityClaims(context.Identity!, "google:" + id, "Google", DateTimeOffset.UtcNow);
                return Task.CompletedTask;
            };
            options.Events.OnRemoteFailure = context =>
            { context.HandleResponse(); context.Response.Redirect("/?authError=google"); return Task.CompletedTask; };
        });
    }
    private static void AddIdentityClaims(ClaimsIdentity identity, string userId, string provider, DateTimeOffset now)
    {
        identity.AddClaim(new("fiddler:user", userId));
        identity.AddClaim(new("fiddler:login", Guid.NewGuid().ToString("N")));
        identity.AddClaim(new("fiddler:provider", provider));
        identity.AddClaim(new("fiddler:expires", now.AddHours(8).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }
    public static void MapWorkspaceAuthentication(this WebApplication app)
    {
        if (app.Services.GetRequiredService<SignInFeatures>().OpenAccess)
            app.Logger.LogWarning("Authentication:Mode is Open: every visitor uses the shared local user without signing in. Do not expose this instance beyond trusted hosts.");
        app.MapGet("/account/sign-in", (SignInFeatures features) => features.Google
            ? Results.Challenge(new AuthenticationProperties { RedirectUri = "/" }, [GoogleDefaults.AuthenticationScheme])
            : Results.Redirect("/?authError=configuration"));
        app.MapPost("/account/development-profile", async (HttpContext context, SignInFeatures features, IAntiforgery antiforgery) =>
        {
            if (!features.DevelopmentProfiles) return Results.NotFound();
            try { await antiforgery.ValidateRequestAsync(context); } catch (AntiforgeryValidationException) { return Results.BadRequest("Invalid request token."); }
            var form = await context.Request.ReadFormAsync();
            string name = form["name"].ToString().Trim();
            if (name.Length < 2 || name.Length > 60 || name.Any(char.IsControl)) return Results.BadRequest("Use a display name between 2 and 60 characters.");
            var identity = new ClaimsIdentity(CookieAuthenticationDefaults.AuthenticationScheme);
            identity.AddClaim(new(ClaimTypes.Name, name));
            AddIdentityClaims(identity, "development:" + Guid.NewGuid().ToString("N"), "Development profile", DateTimeOffset.UtcNow);
            await context.SignInAsync(new ClaimsPrincipal(identity), new AuthenticationProperties { ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8) });
            return Results.LocalRedirect("/");
        });
        app.MapPost("/account/sign-out", async (HttpContext context, SignInFeatures features, UserDirectory users, ResourceLeaseManager leases, IAntiforgery antiforgery) =>
        {
            // Revoking the shared open-access login would lock every visitor out until restart.
            if (features.OpenAccess) return Results.NotFound();
            try { await antiforgery.ValidateRequestAsync(context); } catch (AntiforgeryValidationException) { return Results.BadRequest("Invalid request token."); }
            if (context.User.FindFirstValue("fiddler:login") is { } login && long.TryParse(context.User.FindFirstValue("fiddler:expires"), out long seconds))
                foreach (string session in users.Revoke(login, DateTimeOffset.FromUnixTimeSeconds(seconds))) leases.ReleaseSession(session);
            await context.SignOutAsync();
            return Results.LocalRedirect("/");
        }).RequireAuthorization();
    }
}

/// <summary>Authenticates every request as the shared local user when Authentication:Mode is Open.</summary>
public sealed class OpenAccessAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "OpenAccess";
    public const string UserId = "open:local";
    private static readonly string LoginId = Guid.NewGuid().ToString("N");
    private static readonly string NeverExpires = DateTimeOffset.MaxValue.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var identity = new ClaimsIdentity(SchemeName);
        identity.AddClaim(new(ClaimTypes.Name, "Local user"));
        identity.AddClaim(new("fiddler:user", UserId));
        identity.AddClaim(new("fiddler:login", LoginId));
        identity.AddClaim(new("fiddler:provider", "Open access"));
        identity.AddClaim(new("fiddler:expires", NeverExpires));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
