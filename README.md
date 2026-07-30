# SharpClaw.ModuleHost.OutOfProcess

`SharpClaw.ModuleHost.OutOfProcess` is the .NET host for normal SharpClaw module
execution. SharpClaw starts the host in a separate process for one module
directory. The host loads the module assembly, validates `module.json`, exposes
lifecycle and tool endpoints, and proxies host capabilities through the foreign
module protocol.

A module directory contains a compiled .NET module DLL and a `module.json`
manifest. The manifest identifies the module, names its entry assembly, and
selects the `dotnet` runtime with the `sidecar` host mode. SharpClaw starts the
host with the module directory and private control endpoint. The host accepts
authenticated protocol requests and stops after the parent sends shutdown.

The package includes the executable and its runtime payload. Reference the
package when a module runner must start the standard SharpClaw .NET host for a
module directory. The host loads private module dependencies in a collectible
assembly context while it shares host-owned contract assemblies.

```json
{
  "id": "sample_module",
  "displayName": "Sample Module",
  "version": "1.0.0",
  "toolPrefix": "sample",
  "entryAssembly": "Sample.Module.dll",
  "runtime": "dotnet",
  "hostMode": "sidecar"
}
```
