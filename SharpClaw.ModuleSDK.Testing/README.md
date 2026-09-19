# SharpClaw.ModuleSDK.Testing

`SharpClaw.ModuleSDK.Testing` compiles real package registrations and runs their behavior through the production `SharpClaw.Core` graph. It keeps scope validation, effect grants, sensitive approvals, outcome rules, cancellation, and handler disposal active.

## Build a Test Host

Pass the real `package.json` path when package metadata is part of the test. The builder uses `PackageManifestLoader` and compiles the manifest host mode. The builder rejects an absent or unsupported `hostMode`.

```csharp
await using var host = new SharpClawModuleTestBuilder()
    .ConfigureServices(services => services.AddSingleton(testCapture))
    .AddRegistration(new SampleAuthorizationModule(), manifestPath)
    .ApproveSensitiveContributions("sample_authorization")
    .UseExecutionContext(caller, features)
    .Build();
```

## Execution Methods

| Method | Use |
| --- | --- |
| `Action(...).WithTerminal(...)` | Test a package hook around a host-owned terminal. |
| `ActionEntry(...)` | Test the package-owned registered terminal. |
| `Event(...)` | Dispatch an event through the compiled Core graph. |
| `InvokeToolAsync(...)` | Invoke a tool with caller, feature, and conversation authority. |
| `InvokeCliAsync(...)` | Resolve and invoke one scoped CLI handler. |
| `InvokeHttpAsync(...)` | Resolve and invoke one scoped HTTP handler. |
| `InvokeWebSocketAsync(...)` | Resolve and invoke one scoped WebSocket handler. |
| `InScopeAsync(...)` | Resolve package services in a bounded asynchronous scope. |
| `StartAsync` and `StopAsync` | Test lifecycle behavior through Core lifecycle actions. |

`ActionEntry` requires one matching action definition and one matching terminal registration. Each application method also requires one exact contribution identity. The host resolves each handler in a new asynchronous scope. Scoped handlers do not become hidden singletons.

Call `UseHostActionEntry` when application behavior enters a host-owned action. The test host gives that instance priority over package service registrations. Without this call, host action entry requests fail closed.

## Sensitive Behavior

Call `ApproveSensitiveContributions` only for each package identity that production explicitly approves. The test host creates exact schema-bound approvals. It does not disable sensitive checks or grant wildcard authority.

## Authorization Example

The following call executes the registered authorization terminal and all approved restrictions. A restriction denial returns the declared failed outcome before the policy terminal runs.

```csharp
var outcome = await host.ActionEntry(
        AuthorizationProtocol.Evaluate,
        new AuthorizationRequest(
            "documents.read",
            new AuthorizationResource("document", documentId)))
    .RunAsync(cancellationToken);
```

This package is test-only. Production hosts do not reference it.
