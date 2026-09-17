# SharpClaw.SidecarHost.InProcess

`SharpClaw.SidecarHost.InProcess` loads one explicitly trusted .NET package into a collectible assembly context. It parses `package.json`, validates `hostMode: in-process`, creates the package instance, and compiles the same ModuleSDK graph used by sidecar hosting.

## Host Composition

`InProcessRegistrationHost.LoadAsync` returns validated service descriptors and the compiled graph. The application host adds those descriptors to its service collection, builds its provider, and calls `Bind`. `InProcessModuleInvoker` resolves scoped handlers from that provider for each invocation.

```csharp
await using var package = await InProcessRegistrationHost.LoadAsync(packageDirectory);

var services = new ServiceCollection();
foreach (var descriptor in package.ServiceDescriptors)
    ((ICollection<ServiceDescriptor>)services).Add(descriptor);

await using var provider = services.BuildServiceProvider(new ServiceProviderOptions
{
    ValidateOnBuild = true,
    ValidateScopes = true,
});

package.Bind(provider);
await package.StartAsync(hostVersion, features, cancellationToken);
```

## Authority

The host issues action, endpoint, CLI, tool, and storage authority. The invoker does not create caller identity or substitute an outcome. It resolves only the handler selected by the compiled graph.

## Lifetime

Create one registration host for each loaded package. Stop package lifecycle behavior before disposal. Disposal unloads the collectible context after the package has released its references.

Out-of-process hosting remains the default for optional packages. Use this package only for an explicit in-process trust decision.
