using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Kasper.Application.Abstractions;
using Kasper.Application.Contracts;

namespace Kasper.Infrastructure.Runtime;

/// <summary>In-process pub/sub for live UI updates, plus the worker wake-up signal.</summary>
public sealed class WorkflowEvents : IWorkflowEvents
{
    private readonly ConcurrentDictionary<Guid, Channel<WorkflowEventDto>> _subscribers = new();
    private readonly SemaphoreSlim _signal = new(0, 1);

    public void Publish(WorkflowEventDto e)
    {
        foreach (var channel in _subscribers.Values) channel.Writer.TryWrite(e);
    }

    public async IAsyncEnumerable<WorkflowEventDto> SubscribeAsync([EnumeratorCancellation] CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<WorkflowEventDto>(new BoundedChannelOptions(100) { FullMode = BoundedChannelFullMode.DropOldest });
        _subscribers[id] = channel;
        try
        {
            await foreach (var e in channel.Reader.ReadAllAsync(ct)) yield return e;
        }
        finally
        {
            _subscribers.TryRemove(id, out _);
        }
    }

    public void SignalWorker()
    {
        try { _signal.Release(); } catch (SemaphoreFullException) { }
    }

    public async Task WaitForSignalAsync(TimeSpan timeout, CancellationToken ct)
    {
        try { await _signal.WaitAsync(timeout, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
}

/// <summary>Cancellation handles for workflows that are executing right now.</summary>
public sealed class RunningWorkflows
{
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _running = new();

    public CancellationTokenSource Register(Guid id, CancellationToken stopping)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        _running[id] = cts;
        return cts;
    }

    public void Unregister(Guid id)
    {
        if (_running.TryRemove(id, out var cts)) cts.Dispose();
    }

    public bool Cancel(Guid id)
    {
        if (!_running.TryGetValue(id, out var cts)) return false;
        try { cts.Cancel(); } catch (ObjectDisposedException) { }
        return true;
    }
}
