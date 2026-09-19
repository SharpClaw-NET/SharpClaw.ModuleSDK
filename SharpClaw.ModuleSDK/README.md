# SharpClaw.ModuleSDK

`SharpClaw.ModuleSDK` gives package authors one dependency-injection surface for services, actions, events, tools, storage, chat behavior, contracts, endpoints, and CLI commands. `SharpClawModuleCompiler` validates the complete contribution graph before the host starts package code.

## Minimal Package

Implement `ISharpClawModule`. Keep the identity stable, and register all behavior through the supplied `IServiceCollection`. Normal constructor injection remains available to every handler.

```csharp
public sealed class AuditModule : ISharpClawModule
{
    public ModuleIdentity Identity { get; } = new(
        "audit",
        "Audit",
        "audit");

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddScoped<AuditStore>();
        services.AddTool<AuditLookupTool>(AuditTools.Lookup);
    }
}
```

## Registration Map

| API | Result |
| --- | --- |
| `AddAction(...).UseTerminal<T>` | One typed action and one stable terminal identity. |
| `OnAction`, `OnActionCategory`, `OnAnyAction` | Exact, category, or wildcard action interception. |
| `AddEvent` and event hook extensions | Typed event definitions and ordered event behavior. |
| `AddTool<T>` | One scoped `IToolHandler`. |
| `AddHttpEndpoint<T>` | One scoped authenticated HTTP handler. |
| `AddWebSocketEndpoint<T>` | One scoped authenticated WebSocket handler. |
| `AddCliCommand<T>` | One scoped CLI handler. |
| `ExportContract<T>` and `RequireContract<T>` | One exact shared service boundary. |
| `AddStorage` | One declared storage contract through the host gateway. |

## Manifest Authority

Use `PackageManifestLoader` to read `package.json` in tools and tests. It uses the same bounded parser as both production hosts. `SharpClawModuleCompiler.Compile` compares the manifest identity and requested effects with the code declarations.

## Low-Level Control

Action descriptors retain capability, timeout, safe-point, repeat, continuation, schema, and sensitive-data controls. Hook ordering remains explicit through `HookOrdering`. Host-issued `ActionContext`, `ToolInvocation`, endpoint requests, and CLI invocations expose the authenticated caller, features, trace, deadline, and cancellation authority.

## Authorization

`AddAuthorizationPolicy<TPolicy>` exports the neutral authorization contract and registers its typed action terminal. `RequireAuthorization` consumes the active provider. `AddAuthorizationRestriction<TRestriction>` adds an ordered restriction that can preserve or deny access but cannot grant it.

Use `SharpClaw.ModuleSDK.Testing` to compile the real manifest. The test host invokes actions, events, tools, commands, and endpoints through production dispatch maps.
