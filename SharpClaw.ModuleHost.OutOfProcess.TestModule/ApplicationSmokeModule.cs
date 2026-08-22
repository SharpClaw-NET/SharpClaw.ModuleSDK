using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Modules;
using SharpClaw.ModuleSDK;

namespace SharpClaw.ModuleHost.OutOfProcess.TestModule;

public sealed record ApplicationSmokeAction(string Mode, string Value);

public sealed record ApplicationSmokeResult(string Value);

public sealed record ApplicationChildAction(string Name, int Count);

public sealed record ApplicationChildResult(string Value);

public sealed record AgentsJobImportAction(string JobId);

public sealed record AgentsJobImportResult(string Value);

public sealed class ApplicationSmokeModule : ISharpClawModule, ISharpClawApplicationModule
{
    public const string Id = "application_smoke_module";
    public const string HostActionHookId = "application.host.authorization";
    public const string ChildActionHookId = "application.child.authorization";
    public const string OwnedActionHookId = "application.owned.authorization";
    public const string CliName = "application.inspect";
    public const string CapabilityCliName = "application.capabilities";
    public const string HostEntryCliName = "application.host-entry";
    public const string NestedHostEntryCliName = "application.host-entry-nested";
    public const string HostEntryToolName = "application.host-entry-tool";
    public const string SelfOwnedEntryCliName = "application.self-owned-entry";
    public const string ScopedEndpointProbeEnvironmentVariable =
        "SHARPCLAW_MODULESDK_SCOPED_ENDPOINT_PROBE";
    public const string ScopedTerminalProbeEnvironmentVariable =
        "SHARPCLAW_MODULESDK_SCOPED_TERMINAL_PROBE";
    public const string AgentsJobImportActionHookId = "agents.job.import.authorization";

    public static Guid AgentsJobImportTerminalId { get; } =
        new("44444444-4444-4444-8444-444444444444");

    public static RequestPrincipal HostEntryCaller { get; } =
        new(
            "module-agent",
            "Module Agent",
            new HashSet<string>(["module-agent", "module-operator"], StringComparer.Ordinal));

    public static ExtensionFeatureSet HostEntryFeatures { get; } =
        ExtensionFeatureSet.Empty;

    public static Guid HostEntryTraceId { get; } =
        new("11111111-1111-4111-8111-111111111111");

    public static Guid HostEntryIdempotencyKey { get; } =
        new("22222222-2222-4222-8222-222222222222");

    public const ActionInterceptionCapabilities HostCapabilities =
        ActionInterceptionCapabilities.Inspect
        | ActionInterceptionCapabilities.Wrap
        | ActionInterceptionCapabilities.Cancel;

    public const ActionInterceptionCapabilities UnrequestedCapabilities =
        ActionInterceptionCapabilities.ReplaceResult
        | ActionInterceptionCapabilities.Defer
        | ActionInterceptionCapabilities.Repeat;

    public static ActionDescriptor<ApplicationSmokeAction, ApplicationSmokeResult> HostAction { get; } =
        new(
            new SharpClawActionKey("host.application.smoke"),
            1,
            "application",
            HostCapabilities,
            ContainsSensitiveData: false,
            HasIrreversibleEffects: false,
            new ActionRepeatPolicy(ActionRepeatKind.None, 1, TimeSpan.Zero, "host.application.smoke"),
            ContinuationPolicy: null,
            TimeSpan.FromSeconds(5))
        {
            ProtocolVersionRange = ContractVersionRange.Exact(1),
            SafePoints = [ActionSafePoint.BeforeContinuation, ActionSafePoint.BeforeTerminal],
            InputSchema = ModuleSchemaIdentity.ActionInput(
                new SharpClawActionKey("host.application.smoke"),
                1,
                typeof(ApplicationSmokeAction)),
            ResultSchema = ModuleSchemaIdentity.ActionResult(
                new SharpClawActionKey("host.application.smoke"),
                1,
                typeof(ApplicationSmokeResult)),
        };

    public static ActionDescriptor<ApplicationSmokeAction, ApplicationSmokeResult> OwnedAction { get; } =
        HostAction with
        {
            Key = new SharpClawActionKey("module.application.smoke"),
            InputSchema = ModuleSchemaIdentity.ActionInput(
                new SharpClawActionKey("module.application.smoke"),
                1,
                typeof(ApplicationSmokeAction)),
            ResultSchema = ModuleSchemaIdentity.ActionResult(
                new SharpClawActionKey("module.application.smoke"),
                1,
                typeof(ApplicationSmokeResult)),
        };

    public static ActionDescriptor<ApplicationChildAction, ApplicationChildResult> ChildAction { get; } =
        new(
            new SharpClawActionKey("host.application.child"),
            2,
            "application-child",
            HostCapabilities,
            ContainsSensitiveData: false,
            HasIrreversibleEffects: false,
            new ActionRepeatPolicy(ActionRepeatKind.None, 1, TimeSpan.Zero, "host.application.child"),
            ContinuationPolicy: null,
            TimeSpan.FromSeconds(5))
        {
            ProtocolVersionRange = ContractVersionRange.Exact(1),
            SafePoints = [ActionSafePoint.BeforeContinuation, ActionSafePoint.BeforeTerminal],
            InputSchema = ModuleSchemaIdentity.ActionInput(
                new SharpClawActionKey("host.application.child"),
                2,
                typeof(ApplicationChildAction)),
            ResultSchema = ModuleSchemaIdentity.ActionResult(
                new SharpClawActionKey("host.application.child"),
                2,
                typeof(ApplicationChildResult)),
        };

    public static ActionDescriptor<AgentsJobImportAction, AgentsJobImportResult> AgentsJobImportAction { get; } =
        new(
            new SharpClawActionKey("agents.job.import"),
            1,
            "agents",
            ActionInterceptionCapabilities.Inspect,
            ContainsSensitiveData: false,
            HasIrreversibleEffects: false,
            new ActionRepeatPolicy(ActionRepeatKind.None, 1, TimeSpan.Zero, "agents.job.import"),
            ContinuationPolicy: null,
            TimeSpan.FromSeconds(5))
        {
            ProtocolVersionRange = ContractVersionRange.Exact(1),
            SafePoints = [ActionSafePoint.BeforeTerminal],
            InputSchema = ModuleSchemaIdentity.ActionInput(
                new SharpClawActionKey("agents.job.import"),
                1,
                typeof(AgentsJobImportAction)),
            ResultSchema = ModuleSchemaIdentity.ActionResult(
                new SharpClawActionKey("agents.job.import"),
                1,
                typeof(AgentsJobImportResult)),
        };

    public ModuleIdentity Identity { get; } = new(Id, "Application Smoke", "appsmoke");

    public void Configure(ISharpClawModuleBuilder module)
    {
        module.Actions.Add(OwnedAction);
        module.Actions.Add(AgentsJobImportAction);
        module.Services.AddScoped<ScopedEndpointResource>();
        module.Services.AddScoped<ScopedTerminalResource>();
        module.Storage.Add(new ModuleStorageContractDescriptor(
            Id,
            "application-store",
            [new ModuleStorageOperationDescriptor("echo")],
            "Application capability smoke storage."));
        module.Hooks.For(HostAction).Use<AuthorizationHook>(
            HostCapabilities,
            new HookOrdering(HostActionHookId));
        module.Hooks.For(ChildAction).Use<ChildAuthorizationHook>(
            HostCapabilities,
            new HookOrdering(ChildActionHookId));
        module.Hooks.For(OwnedAction).Use<AuthorizationHook>(
            HostCapabilities,
            new HookOrdering(OwnedActionHookId));
        module.AddActionEntry<AgentsJobImportAction, AgentsJobImportResult, AgentsJobImportTerminal>(
            AgentsJobImportAction,
            AgentsJobImportTerminalId);
        module.Tools.Add<HostEntryToolHandler>(new ToolDescriptor(
            HostEntryToolName,
            "Invokes the host-owned application action through the tool ingress.",
            JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new
                {
                    value = new { type = "string" },
                },
                required = new[] { "value" },
            })));
    }

    public void ConfigureApplication(ISharpClawApplicationBuilder application)
    {
        application.Endpoints.Add<ApplicationEndpoint>();
        application.Endpoints.Add<ScopedEndpoint>();
        application.Cli.Add<ApplicationCliHandler>(new ModuleCliCommandDescriptor(
            CliName,
            ["app-inspect"],
            "Returns the module and graph identity.",
            new JsonSchemaReference("application.inspect.input", 1, "application-input"),
            new JsonSchemaReference("application.inspect.result", 1, "application-result")));
        application.Cli.Add<CapabilityCliHandler>(new ModuleCliCommandDescriptor(
            CapabilityCliName,
            ["app-capabilities"],
            "Exercises host-owned action and storage capabilities.",
            new JsonSchemaReference("application.capabilities.input", 1, "application-input"),
            new JsonSchemaReference("application.capabilities.result", 1, "application-result")));
        application.Cli.Add<HostEntryCliHandler>(new ModuleCliCommandDescriptor(
            HostEntryCliName,
            ["app-host-entry"],
            "Exercises the host-owned action entry.",
            new JsonSchemaReference("application.host-entry.input", 1, "application-input"),
            new JsonSchemaReference("application.host-entry.result", 1, "application-result")));
        application.Cli.Add<HostEntryCliHandler>(new ModuleCliCommandDescriptor(
            NestedHostEntryCliName,
            ["app-host-entry-nested"],
            "Exercises nested host-owned action entries.",
            new JsonSchemaReference("application.host-entry.nested.input", 1, "application-input"),
            new JsonSchemaReference("application.host-entry.nested.result", 1, "application-result")));
        application.Cli.Add<SelfOwnedEntryCliHandler>(new ModuleCliCommandDescriptor(
            SelfOwnedEntryCliName,
            ["app-self-owned-entry"],
            "Exercises a module-owned action entry through the authenticated host boundary.",
            new JsonSchemaReference("application.self-owned-entry.input", 1, "application-input"),
            new JsonSchemaReference("application.self-owned-entry.result", 1, "application-result")));
    }

    public sealed class AuthorizationHook : IActionInterceptor<ApplicationSmokeAction, ApplicationSmokeResult>
    {
        public async ValueTask<IActionOutcome<ApplicationSmokeResult>> InvokeAsync(
            ActionContext<ApplicationSmokeAction> context,
            IActionControl<ApplicationSmokeAction, ApplicationSmokeResult> control,
            CancellationToken ct) =>
            string.Equals(context.Action.Mode, "deny", StringComparison.Ordinal)
                ? control.Cancel("application_denied", "The application smoke request was denied.")
                : await control.ProceedAsync(ct);
    }

    public sealed class ChildAuthorizationHook : IActionInterceptor<ApplicationChildAction, ApplicationChildResult>
    {
        public ValueTask<IActionOutcome<ApplicationChildResult>> InvokeAsync(
            ActionContext<ApplicationChildAction> context,
            IActionControl<ApplicationChildAction, ApplicationChildResult> control,
            CancellationToken ct) =>
            control.ProceedAsync(ct);
    }

    public sealed class ApplicationCliHandler(
        ISharpClawModule module,
        ModuleContributionGraph graph) : IModuleCliHandler
    {
        public ValueTask<ModuleCliResult> ExecuteAsync(
            ModuleCliInvocation invocation,
            CancellationToken ct) =>
            ValueTask.FromResult(new ModuleCliResult(
                true,
                [new ModuleCliOutput(
                    "stdout",
                    $"{module.Identity.Id}|{graph.Identity.Id}|{graph.ContractHash}|{invocation.Command}")]));
    }

    public sealed class ApplicationEndpoint : IModuleEndpointHandler
    {
        public async ValueTask<ModuleEndpointResult> InvokeAsync(
            HostEndpointInvocation invocation,
            IHostActionEntry hostActionEntry,
            CancellationToken cancellationToken)
        {
            var outcome = await hostActionEntry.InvokeAsync<ApplicationSmokeAction, ApplicationSmokeResult>(
                new HostActionEntryRequest<ApplicationSmokeAction, ApplicationSmokeResult>(
                    HostAction,
                    new ApplicationSmokeAction("endpoint", "action"),
                    invocation.HostActionContext),
                new HostActionTerminal(),
                cancellationToken);
            return ModuleEndpointResult.Success(
                JsonSerializer.SerializeToElement(new
                {
                    outcome = outcome.Kind.ToString(),
                    value = outcome.Result?.Value,
                }));
        }
    }

    public sealed class ScopedEndpoint(ScopedEndpointResource resource) : IModuleEndpointHandler
    {
        public ValueTask<ModuleEndpointResult> InvokeAsync(
            HostEndpointInvocation invocation,
            IHostActionEntry hostActionEntry,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(ModuleEndpointResult.Success(
                JsonSerializer.SerializeToElement(new { state = resource.State })));
    }

    public sealed class ScopedEndpointResource : IDisposable
    {
        private int _disposed;

        public string State => Volatile.Read(ref _disposed) == 0 ? "active" : "disposed";

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            var path = Environment.GetEnvironmentVariable(
                ScopedEndpointProbeEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(path))
                File.WriteAllText(path, State);
        }
    }

    public sealed class AgentsJobImportTerminal(ScopedTerminalResource resource) :
        IHostActionEntryTerminal<AgentsJobImportAction, AgentsJobImportResult>
    {
        public Guid TerminalId => AgentsJobImportTerminalId;

        public ValueTask<AgentsJobImportResult> InvokeAsync(
            ActionContext<AgentsJobImportAction> context,
            CancellationToken cancellationToken)
        {
            var snapshotHash = SidecarCapabilityTransportValidation
                .ComputeSnapshotHash(context.Snapshot);
            return ValueTask.FromResult(
                new AgentsJobImportResult(
                    $"imported:{context.Action.JobId}:caller={context.Caller.SubjectId}:snapshot={snapshotHash}:scope={resource.InstanceId}:state={resource.State}"));
        }
    }

    public sealed class ScopedTerminalResource : IDisposable
    {
        private static int _nextInstanceId;
        private int _disposed;

        public ScopedTerminalResource() =>
            InstanceId = Interlocked.Increment(ref _nextInstanceId);

        public int InstanceId { get; }

        public string State => Volatile.Read(ref _disposed) == 0 ? "active" : "disposed";

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            var path = Environment.GetEnvironmentVariable(
                ScopedTerminalProbeEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(path))
                File.WriteAllText(path, $"disposed:{InstanceId}");
        }
    }

    public sealed class BadAgentsJobImportTerminal :
        IHostActionEntryTerminal<AgentsJobImportAction, AgentsJobImportResult>
    {
        public Guid TerminalId { get; } = new("55555555-5555-4555-8555-555555555555");

        public ValueTask<AgentsJobImportResult> InvokeAsync(
            ActionContext<AgentsJobImportAction> context,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new AgentsJobImportResult("unexpected-terminal"));
    }

    public sealed class CliAgentsJobImportTerminal :
        IHostActionEntryTerminal<AgentsJobImportAction, AgentsJobImportResult>
    {
        public Guid TerminalId => AgentsJobImportTerminalId;

        public ValueTask<AgentsJobImportResult> InvokeAsync(
            ActionContext<AgentsJobImportAction> context,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(
                new AgentsJobImportResult(
                    $"imported:{context.Action.JobId}:caller={context.Caller.SubjectId}"));
    }

    public sealed class HostEntryCliHandler(IHostActionEntry hostActionEntry) : IModuleCliHandler
    {
        public async ValueTask<ModuleCliResult> ExecuteAsync(
            ModuleCliInvocation invocation,
            CancellationToken ct)
        {
            try
            {
                var hostActionContext = invocation.HostActionContext;
                switch (invocation.Arguments.FirstOrDefault())
                {
                    case "caller":
                        hostActionContext = hostActionContext with
                        {
                            Caller = new RequestPrincipal(
                            "spoofed-agent",
                                hostActionContext.Caller.DisplayName,
                                hostActionContext.Caller.Roles,
                                hostActionContext.Caller.IsAuthenticated),
                        };
                        break;
                    case "roles":
                        hostActionContext = hostActionContext with
                        {
                            Caller = new RequestPrincipal(
                                hostActionContext.Caller.SubjectId,
                                hostActionContext.Caller.DisplayName,
                                new HashSet<string>(["spoofed-role"], StringComparer.Ordinal),
                                hostActionContext.Caller.IsAuthenticated),
                        };
                        break;
                    case "authentication":
                        hostActionContext = hostActionContext with
                        {
                            Caller = new RequestPrincipal(
                                hostActionContext.Caller.SubjectId,
                                hostActionContext.Caller.DisplayName,
                                hostActionContext.Caller.Roles,
                                IsAuthenticated: false),
                        };
                        break;
                    case "features":
                        hostActionContext = hostActionContext with
                        {
                            Features = new ExtensionFeatureSet(
                            [
                                new ExtensionFeature(
                                    "application.test-feature",
                                    1,
                                    Id,
                                    128,
                                    JsonSerializer.SerializeToElement(true)),
                            ]),
                        };
                        break;
                    case "trace":
                        hostActionContext = hostActionContext with { TraceId = Guid.NewGuid() };
                        break;
                    case "idempotency":
                        hostActionContext = hostActionContext with { IdempotencyKey = Guid.NewGuid() };
                        break;
                    case "expiry":
                        hostActionContext = hostActionContext with
                        {
                            Deadline = DateTimeOffset.UtcNow.AddMinutes(5),
                        };
                        break;
                }
                var actionMode = invocation.Arguments.FirstOrDefault() switch
                {
                    "nested" => "nested-root",
                    "sequential" => "sequential-root",
                    "cross-descriptor" => "cross-descriptor-root",
                    "cross-sidecar" => "cross-sidecar-root",
                    "cross-sidecar-fail" => "cross-sidecar-fail-root",
                    "cross-sidecar-cancel" => "cross-sidecar-cancel-root",
                    "cross-sidecar-fail-observe" => "cross-sidecar-fail-observe-root",
                    "cross-sidecar-cancel-observe" => "cross-sidecar-cancel-observe-root",
                    "rotation" => "rotation-root",
                    _ => "host-entry",
                };
                var outcome = await hostActionEntry.InvokeAsync<ApplicationSmokeAction, ApplicationSmokeResult>(
                    new HostActionEntryRequest<ApplicationSmokeAction, ApplicationSmokeResult>(
                        HostAction,
                        new ApplicationSmokeAction(actionMode, "action"),
                        hostActionContext),
                    new HostActionTerminal(),
                    ct);
                return new ModuleCliResult(
                    outcome.Kind is ActionOutcomeKind.Completed or ActionOutcomeKind.Deferred,
                    [new ModuleCliOutput(
                        "stdout",
                        $"host-entry:{outcome.Kind}:{outcome.Result?.Value}")],
                    outcome.Error);
            }
            catch (Exception ex)
            {
                return new ModuleCliResult(
                    false,
                    [new ModuleCliOutput("stderr", $"{ex.GetType().FullName}: {ex.Message}")],
                    new ExecutionError("host_entry_failed", ex.Message));
            }
        }
    }

    public sealed class SelfOwnedEntryCliHandler(IHostActionEntry hostActionEntry) : IModuleCliHandler
    {
        public async ValueTask<ModuleCliResult> ExecuteAsync(
            ModuleCliInvocation invocation,
            CancellationToken ct)
        {
            var action = new AgentsJobImportAction(
                invocation.Arguments.FirstOrDefault() ?? "cli-job");
            try
            {
                if (invocation.Arguments.Contains("unauthorized", StringComparer.Ordinal))
                {
                    var rejected = await hostActionEntry.InvokeAsync<
                        AgentsJobImportAction,
                        AgentsJobImportResult>(
                        new HostActionEntryRequest<AgentsJobImportAction, AgentsJobImportResult>(
                            AgentsJobImportAction,
                            action,
                            invocation.HostActionContext),
                        new BadAgentsJobImportTerminal(),
                        ct);
                    return CreateResult(rejected);
                }

                var accepted = await hostActionEntry.InvokeAsync<
                    AgentsJobImportAction,
                    AgentsJobImportResult>(
                    new HostActionEntryRequest<AgentsJobImportAction, AgentsJobImportResult>(
                        AgentsJobImportAction,
                        action,
                        invocation.HostActionContext),
                    new CliAgentsJobImportTerminal(),
                    ct);
                return CreateResult(accepted);
            }
            catch (Exception ex)
            {
                return new ModuleCliResult(
                    false,
                    [new ModuleCliOutput("stderr", $"{ex.GetType().FullName}: {ex.Message}")],
                    new ExecutionError("host_entry_failed", ex.Message));
            }
        }

        private static ModuleCliResult CreateResult(
            IActionOutcome<AgentsJobImportResult> outcome) =>
            new(
                outcome.Kind is ActionOutcomeKind.Completed or ActionOutcomeKind.Deferred,
                [new ModuleCliOutput(
                    "stdout",
                    $"self-owned:{outcome.Kind}:{outcome.Result?.Value}")],
                outcome.Error);
    }

    public sealed class HostEntryToolHandler(IHostActionEntry hostActionEntry) : IToolHandler
    {
        public async ValueTask<ToolResult> InvokeAsync(
            ToolInvocation invocation,
            CancellationToken ct)
        {
            var value = invocation.Arguments.GetProperty("value").GetString() ?? string.Empty;
            var outcome = await hostActionEntry.InvokeAsync<ApplicationSmokeAction, ApplicationSmokeResult>(
                new HostActionEntryRequest<ApplicationSmokeAction, ApplicationSmokeResult>(
                    HostAction,
                    new ApplicationSmokeAction("host-tool", value),
                    invocation.HostActionContext),
                new HostActionTerminal(),
                ct);
            var context = invocation.HostActionContext;
            var roles = context.Caller.Roles is null
                ? string.Empty
                : string.Join(",", context.Caller.Roles.OrderBy(role => role, StringComparer.Ordinal));
            return ToolResult.Text(
                $"host-tool:{outcome.Kind}:{outcome.Result?.Value}"
                + $":caller={context.Caller.SubjectId}:roles={roles}"
                + $":trace={context.TraceId}:idempotency={context.IdempotencyKey}"
                + $":deadline={context.Deadline:O}");
        }
    }

    public sealed class CapabilityCliHandler(
        ISharpClawModule module,
        ModuleContributionGraph graph,
        IModuleStorageGateway storage,
        IActionDispatcher dispatcher) : IModuleCliHandler
    {
        public async ValueTask<ModuleCliResult> ExecuteAsync(
            ModuleCliInvocation invocation,
            CancellationToken ct)
        {
            try
            {
                if (string.Equals(
                        invocation.Arguments.FirstOrDefault(),
                        "single",
                        StringComparison.Ordinal))
                {
                    var singleStorageResult = await storage.InvokeAsync(
                        module.Identity.Id,
                        "application-store",
                        "echo",
                        JsonSerializer.SerializeToElement(new { value = "single" }),
                        ct);
                    return new ModuleCliResult(
                        true,
                        [new ModuleCliOutput(
                            "stdout",
                            $"storage:{singleStorageResult.GetRawText()}")]);
                }

                var contracts = storage.ListContracts();
                var storageResult = await storage.InvokeAsync(
                    module.Identity.Id,
                    "application-store",
                    "echo",
                    JsonSerializer.SerializeToElement(new { value = "storage" }),
                    ct);
                var actionResult = await dispatcher.RunRequiredAsync(
                    HostAction,
                    new ApplicationSmokeAction("capability", "action"),
                    static (context, _) => ValueTask.FromResult(
                        new ApplicationSmokeResult($"terminal:{context.Action.Value}")),
                    new ActionPipelineSnapshot(
                        graph.ContractHash,
                        [new ActionCapabilityGrant(
                            HostAction.Key,
                            HostAction.Version,
                            HostCapabilities | UnrequestedCapabilities,
                            SensitiveApproved: false,
                            AcceptUnknownSchemas: false)],
                        []),
                    ct);
                return new ModuleCliResult(
                    true,
                    [new ModuleCliOutput(
                        "stdout",
                        $"{module.Identity.Id}|{graph.Identity.Id}|{graph.ContractHash}|"
                        + $"contracts:{contracts.Count}|storage:{storageResult.GetRawText()}|"
                        + $"action:{actionResult.Value}")]);
            }
            catch (Exception ex)
            {
                return new ModuleCliResult(
                    false,
                    [new ModuleCliOutput("stderr", $"{ex.GetType().FullName}: {ex.Message}")],
                    new ExecutionError("capability_smoke_failed", ex.Message));
            }
        }
    }

    private sealed class HostActionTerminal : IHostActionEntryTerminal<ApplicationSmokeAction, ApplicationSmokeResult>
    {
        public Guid TerminalId { get; } = Guid.NewGuid();

        public async ValueTask<ApplicationSmokeResult> InvokeAsync(
            ActionContext<ApplicationSmokeAction> context,
            CancellationToken ct) =>
            context.Action.Mode switch
            {
                "nested-root" => new ApplicationSmokeResult(
                    $"nested-root:{(await InvokeNestedAsync(context, "nested-child", ct)).Value}"),
                "nested-child" => new ApplicationSmokeResult(
                    $"nested-child:{(await InvokeNestedAsync(context, "nested-grandchild", ct)).Value}"),
                "cross-descriptor-root" => new ApplicationSmokeResult(
                    $"cross-descriptor:{(await InvokeChildAsync(context, ct)).Value}"),
                "cross-sidecar-root" => new ApplicationSmokeResult(
                    $"cross-sidecar:{(await InvokeCrossSidecarAsync(context, ct)).Value}"),
                "cross-sidecar-fail-root" => new ApplicationSmokeResult(
                    $"cross-sidecar-fail:{(await InvokeCrossSidecarAsync(context, ct)).Value}"),
                "cross-sidecar-cancel-root" => new ApplicationSmokeResult(
                    $"cross-sidecar-cancel:{(await InvokeCrossSidecarAsync(context, ct)).Value}"),
                "cross-sidecar-fail-observe-root" => new ApplicationSmokeResult(
                    $"cross-sidecar-fail-observe:{(await InvokeCrossSidecarAsync(context, ct)).Value}"),
                "cross-sidecar-cancel-observe-root" => new ApplicationSmokeResult(
                    $"cross-sidecar-cancel-observe:{(await InvokeCrossSidecarAsync(context, ct)).Value}"),
                "rotation-root" => new ApplicationSmokeResult(
                    $"rotation-root:{(await InvokeChildAsync(context, ct)).Value}"),
                "sequential-root" => new ApplicationSmokeResult(
                    $"sequential-root:{(await InvokeNestedAsync(context, "sequential-child-one", ct)).Value}|"
                    + $"{(await InvokeNestedAsync(context, "sequential-child-two", ct)).Value}"),
                _ => new ApplicationSmokeResult($"entry-terminal:{context.Action.Value}"),
            };

        private static async ValueTask<ApplicationSmokeResult> InvokeNestedAsync(
            ActionContext<ApplicationSmokeAction> context,
            string mode,
            CancellationToken ct)
        {
            var hostActionEntry = context.HostActionEntry
                ?? throw new InvalidOperationException(
                    "The nested test terminal has no host action entry.");
            var outcome = await hostActionEntry.InvokeNestedAsync<
                ApplicationSmokeAction,
                ApplicationSmokeAction,
                ApplicationSmokeResult>(
                new HostActionEntryNestedRequest<
                    ApplicationSmokeAction,
                    ApplicationSmokeAction,
                    ApplicationSmokeResult>(
                    HostAction.Key,
                    HostAction.Version,
                    new ApplicationSmokeAction(mode, mode),
                    context),
                new HostActionTerminal(),
                ct);
            if (outcome.Kind is not ActionOutcomeKind.Completed || outcome.Result is null)
            {
                throw new InvalidOperationException(
                    $"The nested action returned {outcome.Kind}.");
            }

            return outcome.Result;
        }

        private static async ValueTask<CrossSidecarResult> InvokeCrossSidecarAsync(
            ActionContext<ApplicationSmokeAction> context,
            CancellationToken ct)
        {
            var hostActionEntry = context.HostActionEntry
                ?? throw new InvalidOperationException(
                    "The cross-sidecar test terminal has no host action entry.");
            var outcome = await hostActionEntry.InvokeCrossSidecarAsync(
                new ModuleCrossSidecarActionEntryRequest<
                    CrossSidecarAction,
                    CrossSidecarResult>(
                    CrossSidecarModule.OwnedAction,
                    new CrossSidecarAction(
                        context.Action.Mode switch
                        {
                            "cross-sidecar-fail-root" or "cross-sidecar-fail-observe-root" => "fail",
                            "cross-sidecar-cancel-root" or "cross-sidecar-cancel-observe-root" => "cancel",
                            _ => "target",
                        },
                        context.Action.Value)),
                ct);
            if (context.Action.Mode is
                "cross-sidecar-fail-observe-root" or
                "cross-sidecar-cancel-observe-root")
            {
                return new CrossSidecarResult(
                    $"outcome={outcome.Kind};error={outcome.Error?.Code ?? "none"};"
                    + $"result={outcome.Result?.Value ?? "none"}");
            }
            if (outcome.Kind is not ActionOutcomeKind.Completed || outcome.Result is null)
            {
                throw new InvalidOperationException(
                    $"The cross-sidecar action returned {outcome.Kind}.");
            }

            return outcome.Result;
        }

        private static async ValueTask<ApplicationChildResult> InvokeChildAsync(
            ActionContext<ApplicationSmokeAction> context,
            CancellationToken ct)
        {
            var hostActionEntry = context.HostActionEntry
                ?? throw new InvalidOperationException(
                    "The cross-descriptor test terminal has no host action entry.");
            var outcome = await hostActionEntry.InvokeNestedAsync<
                ApplicationSmokeAction,
                ApplicationChildAction,
                ApplicationChildResult>(
                new HostActionEntryNestedRequest<
                    ApplicationSmokeAction,
                    ApplicationChildAction,
                    ApplicationChildResult>(
                    ChildAction.Key,
                    ChildAction.Version,
                    new ApplicationChildAction("cross-descriptor-child", 7),
                    context),
                new ChildTerminal(),
                ct);
            if (outcome.Kind is not ActionOutcomeKind.Completed || outcome.Result is null)
            {
                throw new InvalidOperationException(
                    $"The cross-descriptor action returned {outcome.Kind}.");
            }

            return outcome.Result;
        }
    }

    private sealed class ChildTerminal : IHostActionEntryTerminal<ApplicationChildAction, ApplicationChildResult>
    {
        public Guid TerminalId { get; } = Guid.NewGuid();

        public ValueTask<ApplicationChildResult> InvokeAsync(
            ActionContext<ApplicationChildAction> context,
            CancellationToken ct) =>
            ValueTask.FromResult(
                new ApplicationChildResult(
                    $"{context.Action.Name}:{context.Action.Count}"));
    }
}

public sealed record CrossSidecarAction(string Operation, string Value);

public sealed record CrossSidecarResult(string Value);

public sealed class CrossSidecarModule : ISharpClawModule
{
    public const string Id = "cross_sidecar_target_module";

    public static Guid TerminalId { get; } =
        new("33333333-3333-4333-8333-333333333333");

    public static ActionDescriptor<CrossSidecarAction, CrossSidecarResult> OwnedAction { get; } =
        new(
            new SharpClawActionKey("target.application.dispatch"),
            1,
            "target-application",
            ActionInterceptionCapabilities.Inspect,
            ContainsSensitiveData: false,
            HasIrreversibleEffects: false,
            new ActionRepeatPolicy(ActionRepeatKind.None, 1, TimeSpan.Zero, "target.application.dispatch"),
            ContinuationPolicy: null,
            TimeSpan.FromSeconds(5))
        {
            ProtocolVersionRange = ContractVersionRange.Exact(1),
            SafePoints = [ActionSafePoint.BeforeTerminal],
            InputSchema = ModuleSchemaIdentity.ActionInput(
                new SharpClawActionKey("target.application.dispatch"),
                1,
                typeof(CrossSidecarAction)),
            ResultSchema = ModuleSchemaIdentity.ActionResult(
                new SharpClawActionKey("target.application.dispatch"),
                1,
                typeof(CrossSidecarResult)),
        };

    public ModuleIdentity Identity { get; } =
        new(Id, "Cross Sidecar Target", "cross-target");

    public void Configure(ISharpClawModuleBuilder module)
    {
        module.Actions.Add(OwnedAction);
        module.AddActionEntry<CrossSidecarAction, CrossSidecarResult, TargetTerminal>(
            OwnedAction,
            TerminalId);
    }

    public sealed class TargetTerminal : IHostActionEntryTerminal<CrossSidecarAction, CrossSidecarResult>
    {
        public Guid TerminalId => CrossSidecarModule.TerminalId;

        public ValueTask<CrossSidecarResult> InvokeAsync(
            ActionContext<CrossSidecarAction> context,
            CancellationToken ct) =>
            context.Action.Operation switch
            {
                "fail" => throw new InvalidOperationException("The target action terminal failed."),
                _ => ValueTask.FromResult(
                    new CrossSidecarResult(
                        $"{CrossSidecarModule.Id}|{context.Action.Operation}|{context.Action.Value}|"
                        + $"depth={context.Depth}|parent={context.ParentInvocationId.HasValue}|"
                        + $"caller={context.Caller.SubjectId}|trace={context.TraceId}|"
                        + $"idempotency={context.IdempotencyKey}")),
            };
    }
}
