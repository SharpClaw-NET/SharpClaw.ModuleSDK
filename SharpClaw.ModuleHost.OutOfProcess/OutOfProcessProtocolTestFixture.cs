#if OUT_OF_PROCESS_PROTOCOL_TEST_FIXTURE
namespace SharpClaw.ModuleHost.OutOfProcess;

internal static class OutOfProcessProtocolTestFixture
{
    private static Func<int, int>? _responseTerminalCallCountTransform;

    internal static void ConfigureResponseTerminalCallCountTransform(
        Func<int, int>? transform) =>
        Interlocked.Exchange(ref _responseTerminalCallCountTransform, transform);

    internal static int TransformResponseTerminalCallCount(int value) =>
        Volatile.Read(ref _responseTerminalCallCountTransform)?.Invoke(value) ?? value;
}
#endif
