# SharpClaw.ModuleHost.OutOfProcess

`SharpClaw.ModuleHost.OutOfProcess` is the default .NET module host path for
SharpClaw. SharpClaw starts the host executable in a separate process for one
module directory, passes the module directory and private control endpoint
through protocol environment variables, and communicates with the module over
the SharpClaw foreign-module protocol. The host loads the module entry assembly,
validates the module identity declared in `module.json`, exposes lifecycle and
tool endpoints, and proxies host capabilities such as providers, module
storage, and agent operations back to the parent SharpClaw runtime.

A practical .NET module package is a module DLL plus `module.json` in the module
directory. The manifest names the module, declares its tool prefix, points at
the entry assembly, and selects the out-of-process .NET host mode. SharpClaw
then runs the module through this host process. In-process hosting exists only
as an opt-in, limited mode for hosts that explicitly enable it; module authors
should expect out-of-process hosting to be the normal execution model.

```json
{
  "id": "sample_module",
  "displayName": "Sample Module",
  "version": "1.0.0",
  "toolPrefix": "sample",
  "entryAssembly": "Sample.Module.dll",
  "runtime": {
    "name": "dotnet",
    "hostMode": "sidecar"
  }
}
```

The current manifest value for the out-of-process .NET host is `sidecar` because
that is the protocol value already understood by SharpClaw. The package name
uses `OutOfProcess` to describe the host role developers interact with: the
module runs outside the parent SharpClaw process, receives a private control
token, and stops when the parent host sends the shutdown protocol request.
