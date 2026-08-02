using System.Text.Json;
using SharpClaw.Contracts.Modules;

namespace SharpClaw.ModuleHost.OutOfProcess.TestModule;

public sealed class ToolLifecycleSmokeModule : ISharpClawModule
{
    public const string Id = "tool_lifecycle_smoke_module";
    public const string ToolName = "smoke.echo";

    public ModuleIdentity Identity { get; } = new(Id, "Tool Lifecycle Smoke", "smoke");

    public bool Started { get; private set; }

    public void Configure(ISharpClawModuleBuilder module) =>
        module.Tools.Add<SmokeTool>(new ToolDescriptor(
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

    public ValueTask StartAsync(ModuleStartContext context, CancellationToken ct)
    {
        if (!Equals(context.Identity, Identity))
            throw new InvalidOperationException("The start identity is invalid.");
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
