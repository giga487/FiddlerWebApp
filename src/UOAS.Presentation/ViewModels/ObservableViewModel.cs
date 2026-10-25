using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace UOAS.Presentation.ViewModels;

public abstract class ObservableViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Changed([CallerMemberName] string name = "") => PropertyChanged?.Invoke(this, new(name));
    protected void ChangedAll() => Changed("");
}

public sealed class AsyncCommand(Func<Task> execute, Func<bool>? canExecute = null) : ICommand
{
    private bool running;
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => !running && (canExecute?.Invoke() ?? true);
    public async Task ExecuteAsync()
    {
        if (!CanExecute(null)) return;
        running = true; Notify();
        try { await execute(); }
        finally { running = false; Notify(); }
    }
    public async void Execute(object? parameter) => await ExecuteAsync();
    public void Notify() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
