namespace IPABridge.Infrastructure;

/// <summary>
/// Coordinates operations that inspect or modify the shared iOS device environment.
/// </summary>
public sealed class DeviceEnvironmentOperationGate
{
    private int _isBusy;

    public event EventHandler? StateChanged;

    public bool IsBusy => Volatile.Read(ref _isBusy) != 0;

    public IDisposable? TryEnter()
    {
        if (Interlocked.CompareExchange(ref _isBusy, 1, 0) != 0)
        {
            return null;
        }

        NotifyStateChanged();
        return new Lease(this);
    }

    private void Exit()
    {
        if (Interlocked.Exchange(ref _isBusy, 0) != 0)
        {
            NotifyStateChanged();
        }
    }

    private void NotifyStateChanged()
    {
        var handlers = StateChanged;
        if (handlers is null)
        {
            return;
        }

        // A view listener must not leave the gate permanently locked or prevent other views from updating.
        foreach (EventHandler handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, EventArgs.Empty);
            }
            catch
            {
                // Command state will be refreshed again on the next gate transition.
            }
        }
    }

    private sealed class Lease(DeviceEnvironmentOperationGate owner) : IDisposable
    {
        private DeviceEnvironmentOperationGate? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Exit();
    }
}
