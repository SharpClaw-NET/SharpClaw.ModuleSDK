using System.Threading.Channels;

namespace SharpClaw.ModuleHost.OutOfProcess;

internal sealed class BoundedExecutionQueue : IAsyncDisposable
{
    private readonly Channel<WorkItem> _channel;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task[] _workers;

    public BoundedExecutionQueue(int capacity, int concurrency)
    {
        if (capacity < 1)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        if (concurrency < 1)
            throw new ArgumentOutOfRangeException(nameof(concurrency));
        _channel = Channel.CreateBounded<WorkItem>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = concurrency == 1,
            SingleWriter = false,
        });
        _workers = Enumerable.Range(0, concurrency)
            .Select(_ => Task.Run(RunAsync))
            .ToArray();
    }

    public bool TrySchedule(
        Func<CancellationToken, Task> operation,
        CancellationToken requestCancellation,
        out Task completion)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var source = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var item = new WorkItem(operation, requestCancellation, source);
        completion = source.Task;
        if (_channel.Writer.TryWrite(item))
            return true;
        source.TrySetResult();
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        _shutdown.Cancel();
        try
        {
            await Task.WhenAll(_workers);
        }
        catch (OperationCanceledException)
        {
        }
        _shutdown.Dispose();
    }

    private async Task RunAsync()
    {
        try
        {
            await foreach (var item in _channel.Reader.ReadAllAsync(_shutdown.Token))
            {
                try
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                        _shutdown.Token,
                        item.RequestCancellation);
                    await item.Operation(linked.Token);
                    item.Completion.TrySetResult();
                }
                catch (OperationCanceledException) when (
                    _shutdown.IsCancellationRequested || item.RequestCancellation.IsCancellationRequested)
                {
                    item.Completion.TrySetCanceled(item.RequestCancellation);
                }
                catch (Exception ex)
                {
                    item.Completion.TrySetException(ex);
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
    }

    private sealed record WorkItem(
        Func<CancellationToken, Task> Operation,
        CancellationToken RequestCancellation,
        TaskCompletionSource Completion);
}
