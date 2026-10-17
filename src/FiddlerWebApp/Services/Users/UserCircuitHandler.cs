using Microsoft.AspNetCore.Components.Server.Circuits;

namespace FiddlerWebApp.Services.Users;

public sealed class UserCircuitHandler(UserSession user) : CircuitHandler
{
    public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
    { user.ConnectionChanged(true); return Task.CompletedTask; }
    public override Task OnConnectionDownAsync(Circuit circuit, CancellationToken cancellationToken)
    { user.ConnectionChanged(false); return Task.CompletedTask; }
    public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken cancellationToken)
    { user.Dispose(); return Task.CompletedTask; }
}
