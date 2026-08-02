# SharpClaw.ModuleHost.InProcess

`SharpClaw.ModuleHost.InProcess` supports the opt-in in-process .NET module
host path for SharpClaw. It loads one `ISharpClawModule` from `module.json`,
compiles its ModuleSDK contribution graph, and starts it in a collectible
assembly load context.

Out-of-process hosting is the normal SharpClaw module execution model.
In-process hosting is a limited mode for hosts that deliberately enable it and
can accept its tighter coupling to the host process.

`InProcessModuleHost.LoadAsync` validates the manifest, loads the module, and
builds its local dependency-injection provider. `InProcessModuleInvoker` then
passes host-issued action and event controls directly to the selected handler.
The adapter does not create substitute outcomes or another dispatch path.

```csharp
using SharpClaw.ModuleHost.InProcess;

await using var host = await InProcessModuleHost.LoadAsync(moduleDirectory);
await host.StartAsync("0.5.0-beta.2");
```

Use one host instance for each loaded module. Dispose the host after the module
stops so the runtime can collect its load context.
