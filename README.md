# SharpClaw Module Host Packages

SharpClaw .NET modules run as a compiled module DLL plus a `module.json`
manifest. For normal module execution, SharpClaw launches the module outside the
parent process through `SharpClaw.ModuleHost.OutOfProcess`. That package
provides the host executable and payload used to load the module assembly,
validate the manifest, expose lifecycle and tool endpoints, and proxy host
capabilities back to SharpClaw over the foreign-module protocol.

`SharpClaw.ModuleHost.InProcess` is for the limited opt-in path where a host
loads a module DLL directly inside its own process. It provides
`ModuleLoadContext`, a collectible assembly load context that resolves the
module's private dependencies while keeping SharpClaw contract assemblies shared
with the host. Use it only when the host deliberately supports in-process module
loading and can accept the tighter coupling that comes with it.

For module developers, the practical shape is the same in either case: build a
.NET assembly that implements the SharpClaw module contracts, place it in the
module directory, and describe it with `module.json`. Unless a host explicitly
opts into in-process loading, expect the module to be run by the out-of-process
host.
