# SharpClaw.ModuleHost.InProcess

`SharpClaw.ModuleHost.InProcess` supports the opt-in in-process .NET module
host path for SharpClaw. It provides `ModuleLoadContext`, a collectible assembly
load context that loads a module entry DLL from its module directory while
delegating host-owned contracts back to the default load context. That keeps
interfaces such as `ISharpClawCoreModule` shared between the host and the loaded
module instead of accidentally loading a second copy of the contract assembly.

Out-of-process hosting is the normal SharpClaw module execution model.
In-process hosting is a limited mode for hosts that deliberately enable it and
can accept its tighter coupling to the host process. Reference this package when
you are writing SharpClaw runtime code, a test harness, or an explicitly
in-process host adapter that needs to load a module DLL directly.

The smallest in-process host shape creates one load context for the module
entry assembly, loads the assembly through that context, and then finds the
module type using the shared contract interfaces already loaded by the host.

```csharp
using System.Reflection;
using SharpClaw.Contracts.Modules;
using SharpClaw.ModuleHost.InProcess;

var entryAssemblyPath = Path.Combine(moduleDirectory, "MyModule.dll");
var loadContext = new ModuleLoadContext(entryAssemblyPath);
var assembly = loadContext.LoadFromAssemblyPath(Path.GetFullPath(entryAssemblyPath));

var moduleType = assembly.GetTypes()
    .Single(type => type.IsAssignableTo(typeof(ISharpClawCoreModule)) && !type.IsAbstract);
```

Use a separate `ModuleLoadContext` for each loaded module instance. When the
module is unloaded, release references to objects and types from that assembly
before calling `Unload`; the runtime can only collect the context after those
references are gone.
