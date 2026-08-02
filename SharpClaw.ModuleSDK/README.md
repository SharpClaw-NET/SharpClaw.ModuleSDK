# SharpClaw.ModuleSDK

`SharpClaw.ModuleSDK` gives .NET module authors one builder surface for module
services, contracts, storage, actions, events, hooks, tools, and application
contributions. The compiler validates the complete module graph before a host
starts the module.

Reference this package when a module implements `ISharpClawModule`. Use the SDK
hook extensions to declare exact, category, or wildcard interception. The
compiler checks these registrations against `module.json` effect requests and
the selected host capabilities.

The compiled graph contains immutable discovery data and dispatch maps. A host
uses those maps to select typed or untyped handlers without a tool-name switch
or a second dispatch path.
