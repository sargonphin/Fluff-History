using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace FluffHistory.Desktop.Presentation.Models;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Changed([CallerMemberName] string name = "") => PropertyChanged?.Invoke(this, new(name));
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string name = "")
    { if (EqualityComparer<T>.Default.Equals(field, value)) return false; field = value; Changed(name); return true; }
}

/// <summary>One invocation per command. Expected failures are presented by the owning command boundary.</summary>
public sealed class AsyncCommand(Func<Task> execute, Func<bool>? canExecute = null) : ObservableObject, ICommand
{
    private bool _busy;
    public bool IsBusy { get => _busy; private set { if (Set(ref _busy, value)) Refresh(); } }
    public event EventHandler? CanExecuteChanged;
    public event Action? Failed;
    public Func<bool>? GroupAvailable { get; set; }
    public bool CanExecute(object? parameter) => !IsBusy && (GroupAvailable?.Invoke() ?? true) && (canExecute?.Invoke() ?? true);
    public async void Execute(object? parameter) => await ExecuteAsync();
    public async Task ExecuteAsync()
    {
        if (!CanExecute(null)) return;
        IsBusy = true;
        try { await execute(); }
        catch (Exception) { Failed?.Invoke(); }
        finally { IsBusy = false; }
    }
    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public static class CommandGroup
{
    public static void Attach(IDesktopContext owner, Action changed, params AsyncCommand[] commands)
    {
        foreach (var command in commands)
        {
            command.GroupAvailable = () => commands.All(c => !c.IsBusy);
            command.PropertyChanged += (_, _) => { changed(); foreach (var item in commands) item.Refresh(); };
            command.Failed += () => { owner.LastOutcome = owner.Strings[TextId.Status_CommandFailure]; owner.Notify(); };
        }
    }
}
