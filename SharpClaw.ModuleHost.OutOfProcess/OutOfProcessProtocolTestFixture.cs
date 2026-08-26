#if OUT_OF_PROCESS_PROTOCOL_TEST_FIXTURE
namespace SharpClaw.ModuleHost.OutOfProcess;

internal static class OutOfProcessProtocolTestFixture
{
    private static Func<int, int>? _responseTerminalCallCountTransform;
    private static Func<CancellationToken, Task>? _beforeActionResponseAsync;
    private static Action<string>? _rebindStateObserver;

    internal static void ConfigureResponseTerminalCallCountTransform(
        Func<int, int>? transform) =>
        Interlocked.Exchange(ref _responseTerminalCallCountTransform, transform);

    internal static int TransformResponseTerminalCallCount(int value) =>
        Volatile.Read(ref _responseTerminalCallCountTransform)?.Invoke(value) ?? value;

    internal static void ConfigureBeforeActionResponseAsync(
        Func<CancellationToken, Task>? callback) =>
        Interlocked.Exchange(ref _beforeActionResponseAsync, callback);

    internal static Task BeforeActionResponseAsync(CancellationToken cancellationToken) =>
        Volatile.Read(ref _beforeActionResponseAsync)?.Invoke(cancellationToken)
        ?? Task.CompletedTask;

    internal static void ConfigureRebindStateObserver(Action<string>? observer) =>
        Interlocked.Exchange(ref _rebindStateObserver, observer);

    internal static void RecordRebindState(string phase, string state)
    {
        try
        {
            Volatile.Read(ref _rebindStateObserver)?.Invoke($"{phase}|{state}");
        }
        catch
        {
        }
    }
}
#endif
