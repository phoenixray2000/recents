namespace Recents.App.Utils;

public class Debouncer : IDisposable
{
    private readonly int _delayMs;
    private readonly Action<string> _action;
    private readonly Dictionary<string, System.Threading.Timer> _timers = new();
    private readonly object _lock = new();

    public Debouncer(int delayMs, Action<string> action)
    {
        _delayMs = delayMs;
        _action  = action;
    }

    public void Trigger(string key)
    {
        lock (_lock)
        {
            if (_timers.TryGetValue(key, out var timer))
            {
                timer.Change(_delayMs, Timeout.Infinite);
            }
            else
            {
                _timers[key] = new System.Threading.Timer(OnTimerFired, key, _delayMs, Timeout.Infinite);
            }
        }
    }

    private void OnTimerFired(object? state)
    {
        var key = (string)state!;
        lock (_lock)
        {
            if (_timers.TryGetValue(key, out var timer))
            {
                timer.Dispose();
                _timers.Remove(key);
            }
        }
        _action(key);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var timer in _timers.Values)
            {
                timer.Dispose();
            }
            _timers.Clear();
        }
    }
}
