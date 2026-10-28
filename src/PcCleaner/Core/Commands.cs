using System.Windows.Input;
using PcCleaner.Core.Localization;

namespace PcCleaner.Core;

public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute is null ? null : _ => canExecute()) { }

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter)
    {
        try
        {
            _execute(parameter);
        }
        catch (Exception ex)
        {
            Log.Error(T._("Ошибка выполнения команды"), ex);
            Dialogs.Error(T._("Ошибка"), ErrorText.Sentence(ex));
        }
    }

    public static void Refresh() => CommandManager.InvalidateRequerySuggested();
}

/// <summary>
/// Async command that blocks re-entry while running and reports exceptions instead of crashing the app.
/// </summary>
public sealed class AsyncRelayCommand : ObservableObject, ICommand
{
    private readonly Func<object?, Task> _execute;
    private readonly Func<object?, bool>? _canExecute;
    private bool _isRunning;

    public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute is null ? null : _ => canExecute()) { }

    public AsyncRelayCommand(Func<object?, Task> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (Set(ref _isRunning, value))
                CommandManager.InvalidateRequerySuggested();
        }
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => !IsRunning && (_canExecute?.Invoke(parameter) ?? true);

    /// <summary>The run started by the last <see cref="Execute"/> (never faults: <see cref="ExecuteAsync"/> reports errors itself).</summary>
    public Task LastTask { get; private set; } = Task.CompletedTask;

    public void Execute(object? parameter) => LastTask = ExecuteAsync(parameter);

    public async Task ExecuteAsync(object? parameter = null)
    {
        if (IsRunning)
            return;
        IsRunning = true;
        try
        {
            await _execute(parameter);
        }
        catch (OperationCanceledException)
        {
            Log.Info(T._("Операция отменена"));
        }
        catch (Exception ex)
        {
            Log.Error(T._("Ошибка выполнения операции"), ex);
            Dialogs.Error(T._("Ошибка"), ErrorText.Sentence(ex));
        }
        finally
        {
            IsRunning = false;
        }
    }
}
