using UOAS.Contracts;
using UOAS.Presentation.Services;
using UOAS.Presentation.ViewModels;
using Xunit;

namespace UOAS.Tests;

public sealed class ViewModelTests
{
    [Fact]
    public async Task WasmViewModelShowsTheOtherDesignerAndDisablesMutationCommands()
    {
        await using var host = new LockIntegrationTests.Host();
        using var aliceHttp = host.CreateClient(); using var bobHttp = host.CreateClient();
        var aliceApi = new AssetApiClient(aliceHttp); var bobApi = new AssetApiClient(bobHttp);
        await using var alice = new WorkspaceViewModel(aliceApi) { DisplayName = "Alice" };
        await using var bob = new WorkspaceViewModel(bobApi) { DisplayName = "Bob" };
        await alice.StartSessionCommand.ExecuteAsync(); await bob.StartSessionCommand.ExecuteAsync();
        await alice.OpenCommand.ExecuteAsync(); await bob.OpenCommand.ExecuteAsync();
        alice.Kind = bob.Kind = ResourceKind.Item;
        await alice.SelectAsync(10); await alice.AcquireCommand.ExecuteAsync();
        await bob.SelectAsync(10);
        Assert.Contains("Alice", bob.LockStatus); Assert.False(bob.OwnsSelection);
        Assert.False(bob.RemoveCommand.CanExecute(null)); Assert.False(bob.ReleaseCommand.CanExecute(null));
        Assert.False(bob.AcquireCommand.CanExecute(null));
        Assert.True(alice.OwnsSelection); Assert.True(alice.RemoveCommand.CanExecute(null));
        await alice.ReleaseCommand.ExecuteAsync();
        await bob.SelectAsync(10);
        await bob.AcquireCommand.ExecuteAsync(); Assert.True(bob.OwnsSelection); Assert.Contains("Bob", bob.LockStatus);
        await bob.CloseCommand.ExecuteAsync(); await alice.CloseCommand.ExecuteAsync();
    }
    [Fact]
    public async Task AsyncCommandPreventsReentryWhileAnOperationIsInFlight()
    {
        var gate = new TaskCompletionSource(); int calls = 0;
        var command = new AsyncCommand(async () => { calls++; await gate.Task; });
        var running = command.ExecuteAsync(); Assert.False(command.CanExecute(null));
        await command.ExecuteAsync(); Assert.Equal(1, calls);
        gate.SetResult(); await running; Assert.True(command.CanExecute(null));
    }
}
