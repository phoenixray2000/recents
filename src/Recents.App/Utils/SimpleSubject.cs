using System.Collections.Concurrent;

namespace Recents.App.Utils;

// 极简的 Subject 实现，避免引入 System.Reactive 依赖
public class SimpleSubject<T> : IObservable<T>, IDisposable
{
    private readonly ConcurrentDictionary<IObserver<T>, byte> _observers = new();

    public IDisposable Subscribe(IObserver<T> observer)
    {
        _observers.TryAdd(observer, 0);
        return new Unsubscriber(_observers, observer);
    }

    public void OnNext(T value)
    {
        foreach (var observer in _observers.Keys)
        {
            observer.OnNext(value);
        }
    }

    public void OnCompleted()
    {
        foreach (var observer in _observers.Keys)
        {
            observer.OnCompleted();
        }
        _observers.Clear();
    }

    public void Dispose()
    {
        _observers.Clear();
    }

    private class Unsubscriber : IDisposable
    {
        private readonly ConcurrentDictionary<IObserver<T>, byte> _obs;
        private readonly IObserver<T> _target;

        public Unsubscriber(ConcurrentDictionary<IObserver<T>, byte> obs, IObserver<T> target)
        {
            _obs    = obs;
            _target = target;
        }

        public void Dispose() => _obs.TryRemove(_target, out _);
    }
}
