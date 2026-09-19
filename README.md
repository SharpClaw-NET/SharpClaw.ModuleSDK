# SharpClaw Module Development

SharpClaw loads optional behavior from manifest-backed .NET packages. A package supplies one `ISharpClawModule`, normal dependency-injection services, and explicit contribution descriptors. The host compiles these declarations into one immutable graph before any contributed behavior starts.

## Package Map

| Package | Purpose |
| --- | --- |
| `SharpClaw.ModuleSDK` | Defines the authoring API and compiles one contribution graph. |
| `SharpClaw.ModuleSDK.Testing` | Runs a compiled graph through the production Core dispatchers. |
| `SharpClaw.SidecarHost.InProcess` | Loads an explicitly trusted package in the host process. |
| `SharpClaw.SidecarHost.OutOfProcess` | Runs one package in the default authenticated sidecar process. |

## One Authoring Surface

`ISharpClawModule.ConfigureServices` is the only registration entry. Add normal services and SharpClaw contributions to the supplied `IServiceCollection`. The compiler rejects duplicate identities, invalid descriptors, undeclared effects, route collisions, and incompatible contracts.

```csharp
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.ModuleSDK;

public sealed class DocumentsModule : ISharpClawModule
{
    public ModuleIdentity Identity { get; } = new(
        "documents",
        "Documents",
        "documents");

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddScoped<DocumentReader>();
        services.AddTool<ReadDocumentTool>(DocumentTools.Read);
    }
}
```

## Manifest

`package.json` is the package authority. It identifies the entry assembly, entry type, host mode, contracts, features, and requested hook effects. `PackageManifestLoader` applies the same bounded, case-sensitive JSON rules as both production hosts.

```json
{
  "id": "documents",
  "displayName": "Documents",
  "version": "1.0.0",
  "toolPrefix": "documents",
  "runtime": "dotnet",
  "hostMode": "sidecar",
  "entryAssembly": "Example.Documents.dll",
  "entryType": "Example.Documents.DocumentsModule",
  "minHostVersion": "0.5.0"
}
```

## Contribution Types

| Need | Registration | Handler or contract |
| --- | --- | --- |
| Tool | `AddTool<THandler>` | `IToolHandler` |
| Typed action | `AddAction(...).UseTerminal<TTerminal>` | `IHostActionEntryTerminal<TAction,TResult>` |
| Action hook | `OnAction`, `OnActionCategory`, or `OnAnyAction` | Typed or untyped action interceptor |
| Event | `AddEvent` | Typed event descriptor and event hooks |
| HTTP route | `AddHttpEndpoint<THandler>` | `IHttpEndpointHandler` |
| WebSocket route | `AddWebSocketEndpoint<THandler>` | `IWebSocketEndpointHandler` |
| CLI command | `AddCliCommand<THandler>` | `ICliHandler` |
| Shared service | `ExportContract<T>` or `RequireContract<T>` | Public shared contract type |
| Storage | `AddStorage` | `IScopedStorageGateway` or `ScopedDocumentStore<T>` |
| Chat behavior | Chat resolver and contributor extensions | Neutral chat contracts |

## Authorization

One package can supply the authoritative `sharpclaw.authorization` policy. Other packages can require it or add restriction-only hooks. `AddAuthorizationPolicy<TPolicy>` supplies the provider, while `AddAuthorizationRestriction<TRestriction>` can only preserve or deny its result.

## Test the Production Shape

`SharpClawModuleTestBuilder` compiles the real package and manifest. It can invoke actions, events, tools, commands, HTTP routes, and WebSocket routes. Each handler runs in a new scope. `UseHostActionEntry` supplies the host-issued action authority for application tests. Sensitive contributions remain denied until the test grants each exact package identity.

```csharp
await using var host = new SharpClawModuleTestBuilder()
    .AddRegistration(new DocumentsAuthorizationModule(), "package.json")
    .ApproveSensitiveContributions("documents_authorization")
    .UseExecutionContext(caller, features)
    .Build();

var decision = await host.ActionEntry(
        AuthorizationProtocol.Evaluate,
        request)
    .RunRequiredAsync();
```

## Hosting

Out-of-process hosting is the default boundary. It gives each package one authenticated capability session and transport-backed host services. In-process hosting is an explicit trust decision and uses a collectible load context. Both modes compile the same contribution graph.

The full authoring guide is in the SharpClaw repository at `docs/guides/Module-Creation-Guide.md`.
