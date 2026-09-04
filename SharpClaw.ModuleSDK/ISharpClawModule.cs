using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Kernel;

namespace SharpClaw.ModuleSDK;

/// <summary>Identifies one discoverable package entry.</summary>
public sealed record ModuleIdentity(string Id, string DisplayName, string ToolPrefix);

/// <summary>Supplies services and lifecycle behavior for one discoverable package entry.</summary>
public interface ISharpClawModule : IServiceLifecycle
{
    ModuleIdentity Identity { get; }

    void ConfigureServices(IServiceCollection services);

    new ValueTask StartAsync(
        ServiceStartContext context,
        CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;

    new ValueTask StopAsync(CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;

    ValueTask IServiceLifecycle.StartAsync(
        ServiceStartContext context,
        CancellationToken cancellationToken) =>
        StartAsync(context, cancellationToken);

    ValueTask IServiceLifecycle.StopAsync(CancellationToken cancellationToken) =>
        StopAsync(cancellationToken);
}
