# SharpClaw.ModuleSDK

`SharpClaw.ModuleSDK` gives .NET module authors one builder surface for module
services, contracts, storage, actions, events, hooks, tools, and application
contributions. The compiler validates the complete module graph before a host
starts the module.

Reference this package when a module implements `IKernelRegistrationSource`. Use the SDK
hook extensions to declare exact, category, or wildcard interception. The
compiler checks these registrations against `package.json` effect requests and
the selected host capabilities.

The compiled graph contains immutable discovery data and dispatch maps. A host
uses those maps to select typed or untyped handlers without a tool-name switch
or a second dispatch path.

Sidecar discovery also carries typed endpoint identities and CLI command
descriptors from `IApplicationRegistrationSource`. The out-of-process host invokes
CLI handlers through the same module service provider and contribution graph.
Use application contributions for host-owned API and CLI integration, and keep
UI contributions for a host mode that explicitly supports them.
