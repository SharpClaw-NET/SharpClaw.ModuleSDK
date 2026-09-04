using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Kernel;
using SharpClaw.ModuleSDK;

namespace SharpClaw.SidecarHost.OutOfProcess.TestRegistration;

public sealed class ToolLifecycleSmokeModule : ISharpClawModule
{
    public const string Id = "tool_lifecycle_smoke_module";
    public const string ToolName = "smoke.echo";

    public ModuleIdentity Identity { get; } = new(Id, "Tool Lifecycle Smoke", "smoke");

    public bool Started { get; private set; }

    public void ConfigureServices(IServiceCollection services) =>
        services.AddTool<SmokeTool>(new ToolDescriptor(
            ToolName,
            "Returns the supplied text and module state.",
            JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new
                {
                    mode = new { type = "string" },
                    text = new { type = "string" },
                },
                required = new[] { "mode" },
            })));

    public ValueTask StartAsync(ServiceStartContext context, CancellationToken ct)
    {
        Started = true;
        return ValueTask.CompletedTask;
    }

    public ValueTask StopAsync(CancellationToken ct)
    {
        Started = false;
        return ValueTask.CompletedTask;
    }

    public sealed class SmokeTool(ISharpClawModule module) : IToolHandler
    {
        public ValueTask<ToolResult> InvokeAsync(
            ToolInvocation invocation,
            CancellationToken ct)
        {
            var mode = invocation.Arguments.GetProperty("mode").GetString();
            if (string.Equals(mode, "fail", StringComparison.Ordinal))
                throw new InvalidOperationException("The test tool failed.");
            if (string.Equals(mode, "cancel", StringComparison.Ordinal))
                throw new OperationCanceledException("The test tool cancelled.");

            var owner = module as ToolLifecycleSmokeModule
                ?? throw new InvalidOperationException("The module service has an invalid type.");
            var text = invocation.Arguments.TryGetProperty("text", out var value)
                ? value.GetString()
                : null;
            var content = string.Equals(mode, "state", StringComparison.Ordinal)
                ? owner.Started ? "started" : "stopped"
                : text;
            return ValueTask.FromResult(ToolResult.Text(content ?? string.Empty));
        }
    }
}
