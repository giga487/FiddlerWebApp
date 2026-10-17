using FiddlerWebApp.Services.Users;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fiddler.Tests;

public sealed class WorkspaceAuthenticationTests
{
    [Fact]
    public void DevelopmentProfilesAreRejectedOutsideDevelopment()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.Configuration["Authentication:DevelopmentProfiles"] = "true";
        Assert.Throws<InvalidOperationException>(() => builder.AddWorkspaceAuthentication());
    }

    [Fact]
    public void MissingGoogleCredentialsDoNotEnableAnImplicitGuestOrDevelopmentAccount()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.Configuration["Authentication:DevelopmentProfiles"] = "false";
        builder.Configuration["Authentication:Google:ClientId"] = "";
        builder.Configuration["Authentication:Google:ClientSecret"] = "";
        builder.AddWorkspaceAuthentication();
        using var services = builder.Services.BuildServiceProvider();
        var features = services.GetRequiredService<SignInFeatures>();
        Assert.False(features.Google);
        Assert.False(features.DevelopmentProfiles);
    }

    [Fact]
    public void OpenModeDisablesSignInProvidersInAnyEnvironment()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.Configuration["Authentication:Mode"] = "Open";
        builder.Configuration["Authentication:Google:ClientId"] = "id";
        builder.Configuration["Authentication:Google:ClientSecret"] = "secret";
        builder.AddWorkspaceAuthentication();
        using var services = builder.Services.BuildServiceProvider();
        var features = services.GetRequiredService<SignInFeatures>();
        Assert.True(features.OpenAccess);
        Assert.False(features.Google);
        Assert.False(features.DevelopmentProfiles);
    }

    [Fact]
    public void UnknownModeIsRejected()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.Configuration["Authentication:Mode"] = "Sometimes";
        Assert.Throws<InvalidOperationException>(() => builder.AddWorkspaceAuthentication());
    }
}