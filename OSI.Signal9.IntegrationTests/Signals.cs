using System.Collections;

namespace OSI.Signal9.IntegrationTests;

/// <summary>Waiting on what a test observes, rather than for a while.</summary>
public static class Signals
{
    /// <summary>
    /// How long a test waits for a signal before it fails as hung. Never part of what a test checks: every signal it
    /// waits for comes as soon as the work behind it is done.
    /// </summary>
    public static readonly TimeSpan HangGuard = TimeSpan.FromMinutes(2);

    /// <summary>Waits for <paramref name="signal"/>, failing the test with <paramref name="what"/> if it never comes.</summary>
    public static async Task SignalledAsync(this Task signal, string what)
    {
        try
        {
            await signal.WaitAsync(HangGuard);
        }
        catch (TimeoutException)
        {
            Assert.Fail($"Never observed {what}.");
        }
    }

    /// <inheritdoc cref="SignalledAsync(Task, string)"/>
    public static async Task<T> SignalledAsync<T>(this Task<T> signal, string what)
    {
        try
        {
            return await signal.WaitAsync(HangGuard);
        }
        catch (TimeoutException)
        {
            Assert.Fail($"Never observed {what}.");
            throw;
        }
    }
}

/// <summary>
/// What a host sent or did, in order, as a test observes it: a test can wait until it sees what it expects, as soon as
/// that happens.
/// </summary>
public sealed class ObservedQueue<T> : IReadOnlyCollection<T>
{
    private readonly List<T> _items = [];
    private readonly List<(Func<IReadOnlyList<T>, bool> Condition, TaskCompletionSource Met)> _waiters = [];

    public void Enqueue(T item)
    {
        lock (_items)
        {
            _items.Add(item);
            foreach (var waiter in _waiters.Where(w => w.Condition(_items)).ToList())
            {
                waiter.Met.TrySetResult();
                _waiters.Remove(waiter);
            }
        }
    }

    /// <summary>Completes once what has been observed so far meets <paramref name="condition"/>.</summary>
    public Task UntilAsync(Func<IReadOnlyList<T>, bool> condition)
    {
        lock (_items)
        {
            if (condition(_items))
                return Task.CompletedTask;
            var met = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((condition, met));
            return met.Task;
        }
    }

    /// <summary>Completes once <paramref name="count"/> of what has been observed match <paramref name="match"/>.</summary>
    public Task CountAsync(Func<T, bool> match, int count = 1) => UntilAsync(items => items.Count(match) >= count);

    public int Count
    {
        get
        {
            lock (_items)
                return _items.Count;
        }
    }

    public IEnumerator<T> GetEnumerator()
    {
        lock (_items)
            return _items.ToList().GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
