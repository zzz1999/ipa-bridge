using System.Windows.Input;

namespace IPABridge.Infrastructure;

public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<object?, Task> _execute;
    private readonly Predicate<object?>? _canExecute;
    private readonly Action<Exception>? _exceptionHandler;
    private bool _isExecuting;

    public AsyncRelayCommand(
        Func<Task> execute,
        Func<bool>? canExecute = null,
        Action<Exception>? exceptionHandler = null)
        : this(
            _ => execute(),
            canExecute is null ? null : _ => canExecute(),
            exceptionHandler)
    {
    }

    public AsyncRelayCommand(
        Func<object?, Task> execute,
        Predicate<object?>? canExecute = null,
        Action<Exception>? exceptionHandler = null)
    {
        ArgumentNullException.ThrowIfNull(execute);
        _execute = execute;
        _canExecute = canExecute;
        _exceptionHandler = exceptionHandler;
    }

    public event EventHandler? CanExecuteChanged;

    public Exception? LastException { get; private set; }

    public bool CanExecute(object? parameter) => !_isExecuting && (_canExecute?.Invoke(parameter) ?? true);

    public async void Execute(object? parameter) => await ExecuteAsync(parameter);

    public async Task ExecuteAsync(object? parameter = null)
    {
        var executionStarted = false;
        try
        {
            if (!CanExecute(parameter))
            {
                return;
            }

            _isExecuting = true;
            executionStarted = true;
            NotifyCanExecuteChanged();
            await _execute(parameter);
            LastException = null;
        }
        catch (Exception exception)
        {
            CaptureException(exception);
        }
        finally
        {
            if (executionStarted)
            {
                _isExecuting = false;
                try
                {
                    NotifyCanExecuteChanged();
                }
                catch (Exception exception)
                {
                    CaptureException(exception);
                }
            }
        }
    }

    public void NotifyCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);

    private void CaptureException(Exception exception)
    {
        LastException = exception;
        if (_exceptionHandler is null)
        {
            return;
        }

        try
        {
            _exceptionHandler(exception);
        }
        catch (Exception handlerException)
        {
            LastException = new AggregateException(exception, handlerException);
        }
    }
}
