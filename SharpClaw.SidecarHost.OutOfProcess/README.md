# SharpClaw.SidecarHost.OutOfProcess

`SharpClaw.SidecarHost.OutOfProcess` is the default .NET package host. It loads one package directory in a separate process and connects it to the parent host through an authenticated capability session.

## Package Shape

The directory contains `package.json`, the entry assembly, and its private dependencies. The manifest uses flat runtime metadata. `hostMode` must be `sidecar`.

```json
{
  "id": "sample",
  "displayName": "Sample",
  "version": "1.0.0",
  "toolPrefix": "sample",
  "runtime": "dotnet",
  "hostMode": "sidecar",
  "entryAssembly": "Sample.Module.dll",
  "entryType": "Sample.Module.SampleModule",
  "minHostVersion": "0.5.0"
}
```

## Execution Boundary

The process validates the manifest, creates the package, and compiles one contribution graph. Each invocation resolves scoped handlers from the package service provider. The process receives host storage and action services only through authenticated transport-backed proxies.

## Application Contributions

`AddHttpEndpoint`, `AddWebSocketEndpoint`, and `AddCliCommand` publish typed descriptors through discovery. The parent host admits each request and issues the exact caller, feature, route, deadline, and cancellation authority. The sidecar invokes the selected handler through the same graph.

## Cross-Package Calls

Use `IHostActionEntry` from an active action, chat, tool, endpoint, or CLI context. Do not create a second root request for nested work. The host preserves the active parent authority and validates the target descriptor.

## Shutdown

The parent host sends the lifecycle stop request and closes the capability session. The sidecar disposes its service provider and unloads its package context before process exit.
