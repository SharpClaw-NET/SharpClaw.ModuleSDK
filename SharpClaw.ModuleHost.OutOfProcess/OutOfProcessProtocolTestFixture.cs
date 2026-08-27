#if OUT_OF_PROCESS_PROTOCOL_TEST_FIXTURE
using SharpClaw.Contracts.Modules;

namespace SharpClaw.ModuleHost.OutOfProcess;

internal static class OutOfProcessProtocolTestFixture
{
    private static Func<int, int>? _responseTerminalCallCountTransform;
    private static Func<CancellationToken, Task>? _beforeActionResponseAsync;
    private static Func<SidecarCapabilityCallIdentity, CancellationToken, Task>?
        _beforeActionResponseForCallAsync;
    private static Func<CancellationToken, Task>? _beforeStorageResponseAsync;
    private static Action<string>? _rebindStateObserver;
    private static Action<SidecarCapabilityCallIdentity>? _callCreatedObserver;
    private static Func<SidecarActionCapabilityRequest, CancellationToken, Task>?
        _beforeOutgoingCallRegistrationAsync;

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

    internal static void ConfigureBeforeActionResponseForCallAsync(
        Func<SidecarCapabilityCallIdentity, CancellationToken, Task>? callback) =>
        Interlocked.Exchange(ref _beforeActionResponseForCallAsync, callback);

    internal static Task BeforeActionResponseAsync(
        SidecarCapabilityCallIdentity call,
        CancellationToken cancellationToken) =>
        Volatile.Read(ref _beforeActionResponseForCallAsync)?.Invoke(call, cancellationToken)
        ?? BeforeActionResponseAsync(cancellationToken);

    internal static void ConfigureBeforeStorageResponseAsync(
        Func<CancellationToken, Task>? callback) =>
        Interlocked.Exchange(ref _beforeStorageResponseAsync, callback);

    internal static Task BeforeStorageResponseAsync(CancellationToken cancellationToken) =>
        Volatile.Read(ref _beforeStorageResponseAsync)?.Invoke(cancellationToken)
        ?? Task.CompletedTask;

    internal static void ConfigureRebindStateObserver(Action<string>? observer) =>
        Interlocked.Exchange(ref _rebindStateObserver, observer);

    internal static void ConfigureCallCreatedObserver(
        Action<SidecarCapabilityCallIdentity>? observer) =>
        Interlocked.Exchange(ref _callCreatedObserver, observer);

    internal static void ConfigureBeforeOutgoingCallRegistrationAsync(
        Func<SidecarActionCapabilityRequest, CancellationToken, Task>? callback) =>
        Interlocked.Exchange(ref _beforeOutgoingCallRegistrationAsync, callback);

    internal static Task BeforeOutgoingCallRegistrationAsync(
        SidecarActionCapabilityRequest request,
        CancellationToken cancellationToken) =>
        Volatile.Read(ref _beforeOutgoingCallRegistrationAsync)?.Invoke(
            request,
            cancellationToken)
        ?? Task.CompletedTask;

    internal static void RecordCallCreated(SidecarCapabilityCallIdentity call)
    {
        try
        {
            Volatile.Read(ref _callCreatedObserver)?.Invoke(call);
        }
        catch
        {
        }
    }

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
