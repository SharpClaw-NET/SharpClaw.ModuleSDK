using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SharpClaw.Contracts.Kernel;
using SharpClaw.Core.Kernel;
using SharpClaw.ModuleSDK;

namespace SharpClaw.SidecarHost.OutOfProcess;

/// <summary>Identifies one host action descriptor for sidecar dispatch.</summary>
public static class OutOfProcessActionDescriptorIdentity
{
    /// <summary>Creates the transport identity for one typed action descriptor.</summary>
    public static SidecarActionDescriptorIdentity Create<TAction, TResult>(
        ActionDescriptor<TAction, TResult> descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        var input = descriptor.InputSchema
            ?? throw new ArgumentException(
                "The action descriptor must declare an input schema.",
                nameof(descriptor));
        var result = descriptor.ResultSchema
            ?? throw new ArgumentException(
                "The action descriptor must declare a result schema.",
                nameof(descriptor));
        var identity = Create(
            descriptor.Key,
            descriptor.Version,
            descriptor.Category,
            typeof(TAction),
            input,
            typeof(TResult),
            result);
        return identity with
        {
            DescriptorHash = HostActionEntryAuthorityValidator.ComputeDescriptorHash(descriptor),
        };
    }

    internal static SidecarActionDescriptorIdentity Create(
        ModuleActionDefinition action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var descriptor = action.Descriptor;
        var identity = Create(
            descriptor.Key,
            descriptor.Version,
            descriptor.Category,
            action.ActionType,
            descriptor.InputSchema,
            action.ResultType,
            descriptor.ResultSchema);
        return identity with
        {
            DescriptorHash = ComputeDescriptorHash(action),
        };
    }

    /// <summary>Creates an identity from exact type and schema metadata.</summary>
    public static SidecarActionDescriptorIdentity Create(
        SharpClawActionKey key,
        int version,
        string category,
        Type inputType,
        JsonSchemaReference inputSchema,
        Type resultType,
        JsonSchemaReference resultSchema)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        ArgumentNullException.ThrowIfNull(inputType);
        ArgumentNullException.ThrowIfNull(inputSchema);
        ArgumentNullException.ThrowIfNull(resultType);
        ArgumentNullException.ThrowIfNull(resultSchema);
        ArgumentException.ThrowIfNullOrWhiteSpace(inputSchema.ContentHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(resultSchema.ContentHash);
        var inputTypeIdentity = inputType.AssemblyQualifiedName
            ?? inputType.FullName
            ?? inputType.Name;
        var resultTypeIdentity = resultType.AssemblyQualifiedName
            ?? resultType.FullName
            ?? resultType.Name;
        return new SidecarActionDescriptorIdentity(
            key,
            version,
            category,
            inputTypeIdentity,
            inputSchema.ContentHash,
            inputSchema.Version,
            resultTypeIdentity,
            resultSchema.ContentHash,
            resultSchema.Version,
            ComputeDescriptorHash(
                key,
                version,
                category,
                inputTypeIdentity,
                inputSchema,
                resultTypeIdentity,
                resultSchema));
    }

    /// <summary>Computes the stable descriptor hash used by both transport peers.</summary>
    public static string ComputeDescriptorHash(
        SharpClawActionKey key,
        int version,
        string category,
        string inputTypeIdentity,
        JsonSchemaReference inputSchema,
        string resultTypeIdentity,
        JsonSchemaReference resultSchema)
    {
        var value = string.Join(
            "|",
            key.Value,
            version,
            category,
            inputTypeIdentity,
            inputSchema.ContractName,
            inputSchema.Version,
            inputSchema.ContentHash,
            resultTypeIdentity,
            resultSchema.ContractName,
            resultSchema.Version,
            resultSchema.ContentHash);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private static string ComputeDescriptorHash(ModuleActionDefinition action)
    {
        var descriptor = action.Descriptor;
        var value = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Key = descriptor.Key.Value,
            Version = descriptor.Version,
            Category = descriptor.Category,
            Capabilities = (int)descriptor.Capabilities,
            ContainsSensitiveData = descriptor.ContainsSensitiveData,
            HasIrreversibleEffects = action.HasIrreversibleEffects,
            Repeat = new
            {
                Kind = action.RepeatPolicy.Kind.ToString(),
                MaximumAttempts = action.RepeatPolicy.MaximumAttempts,
                MinimumBackoffTicks = action.RepeatPolicy.MinimumBackoff.Ticks,
                IdempotencyScope = action.RepeatPolicy.IdempotencyScope,
            },
            Continuation = action.ContinuationPolicy is null
                ? null
                : new
                {
                    MaximumLifetimeTicks = action.ContinuationPolicy.MaximumLifetime.Ticks,
                    Durable = action.ContinuationPolicy.Durable,
                    SingleClaim = action.ContinuationPolicy.SingleClaim,
                },
            DefaultTimeoutTicks = action.DefaultTimeout.Ticks,
            ProtocolMinimum = descriptor.ProtocolVersionRange.Minimum,
            ProtocolMaximum = descriptor.ProtocolVersionRange.Maximum,
            SafePoints = action.SafePoints.Select(point => point.ToString()).ToArray(),
            InputSchema = new
            {
                descriptor.InputSchema.ContractName,
                descriptor.InputSchema.Version,
                descriptor.InputSchema.ContentHash,
            },
            ResultSchema = new
            {
                descriptor.ResultSchema.ContractName,
                descriptor.ResultSchema.Version,
                descriptor.ResultSchema.ContentHash,
            },
            InputTypeIdentity = TypeIdentity(action.ActionType),
            ResultTypeIdentity = TypeIdentity(action.ResultType),
        });
        return Convert.ToHexString(SHA256.HashData(value));
    }

    private static string TypeIdentity(Type type) =>
        type.AssemblyQualifiedName ?? type.FullName ?? type.Name;

    internal static bool Matches(
        SidecarActionDescriptorIdentity expected,
        SidecarActionDescriptorIdentity actual) =>
        expected.Key == actual.Key
        && expected.Version == actual.Version
        && string.Equals(expected.Category, actual.Category, StringComparison.Ordinal)
        && string.Equals(expected.InputTypeIdentity, actual.InputTypeIdentity, StringComparison.Ordinal)
        && string.Equals(expected.InputSchemaHash, actual.InputSchemaHash, StringComparison.Ordinal)
        && expected.InputSchemaVersion == actual.InputSchemaVersion
        && string.Equals(expected.ResultTypeIdentity, actual.ResultTypeIdentity, StringComparison.Ordinal)
        && string.Equals(expected.ResultSchemaHash, actual.ResultSchemaHash, StringComparison.Ordinal)
        && expected.ResultSchemaVersion == actual.ResultSchemaVersion
        && string.Equals(expected.DescriptorHash, actual.DescriptorHash, StringComparison.Ordinal);
}

/// <summary>Stores the typed action descriptors available to the host dispatcher.</summary>
public sealed class OutOfProcessActionDescriptorCatalog
{
    private readonly Dictionary<string, Registration> _registrations = new(StringComparer.Ordinal);

    /// <summary>Adds one typed descriptor to the immutable host lookup set.</summary>
    public void Add<TAction, TResult>(ActionDescriptor<TAction, TResult> descriptor)
        => Add<TAction, TResult>(descriptor, hostTerminal: null);

    /// <summary>Adds one typed descriptor and its host-owned terminal entry.</summary>
    public void Add<TAction, TResult>(
        ActionDescriptor<TAction, TResult> descriptor,
        Func<ActionContext<TAction>, CancellationToken, ValueTask<TResult>>? hostTerminal)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        var identity = OutOfProcessActionDescriptorIdentity.Create(descriptor);
        var registration = new Registration(
            identity,
            descriptor,
            typeof(TAction),
            typeof(TResult),
            (session, request, cancellationToken) =>
                session.DispatchAsync(
                    descriptor,
                    identity,
                    request,
                    hostTerminal,
                    cancellationToken),
            (session, carrier, request, terminal, hostContext, cancellationToken) =>
                session.DispatchCrossSidecarAsync(
                    descriptor,
                    identity,
                    carrier,
                    request,
                    terminal,
                    hostContext,
                    cancellationToken));
        if (!_registrations.TryAdd(Key(identity), registration))
        {
            throw new InvalidOperationException(
                $"The host action descriptor '{identity.Key.Value}:{identity.Version}' is already registered.");
        }
    }

    /// <summary>Adds one discovered descriptor without loading its optional contract types.</summary>
    public void Add(
        SidecarActionDefinition definition,
        SidecarActionDescriptorIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(identity);
        if (!SidecarExternalActionDispatchAuthorityValidator.DescriptorMatchesDefinition(
                identity,
                definition))
        {
            throw new ArgumentException(
                "The discovered action definition does not match its transport identity.",
                nameof(identity));
        }

        var registration = new Registration(
            identity,
            definition,
            typeof(JsonElement),
            typeof(JsonElement),
            (session, request, cancellationToken) =>
                session.DispatchSerializedAsync(
                    definition,
                    identity,
                    request,
                    cancellationToken),
            (session, carrier, request, terminal, hostContext, cancellationToken) =>
                session.DispatchCrossSidecarSerializedAsync(
                    definition,
                    identity,
                    carrier,
                    request,
                    terminal,
                    hostContext,
                    cancellationToken));
        if (!_registrations.TryAdd(Key(identity), registration))
        {
            throw new InvalidOperationException(
                $"The host action descriptor '{identity.Key.Value}:{identity.Version}' is already registered.");
        }
    }

    internal bool TryGet(
        SidecarActionDescriptorIdentity identity,
        out Registration registration)
    {
        if (!_registrations.TryGetValue(Key(identity), out registration!))
            return false;

        if (OutOfProcessActionDescriptorIdentity.Matches(registration.Identity, identity))
            return true;

        registration = null!;
        return false;
    }

    internal bool TryResolve(
        SharpClawActionKey key,
        int version,
        out Registration registration)
    {
        Registration? match = null;
        foreach (var candidate in _registrations.Values)
        {
            if (candidate.Identity.Key != key || candidate.Identity.Version != version)
                continue;

            if (match is not null)
            {
                registration = null!;
                return false;
            }

            match = candidate;
        }

        registration = match!;
        return match is not null;
    }

    private static string Key(SidecarActionDescriptorIdentity identity) =>
        $"{identity.Key.Value}|{identity.Version}|{identity.DescriptorHash}";

    internal sealed record Registration(
        SidecarActionDescriptorIdentity Identity,
        object Descriptor,
        Type ActionType,
        Type ResultType,
        Func<
            OutOfProcessCapabilityHostSession,
            SidecarActionCapabilityRequest,
            CancellationToken,
            ValueTask<OutOfProcessActionDispatchResult>> Dispatch,
        Func<
            OutOfProcessCapabilityHostSession,
            SidecarCrossSidecarActionEntryCarrier,
            SidecarActionTerminalTransportRequest,
            SidecarActionTerminalRegistration,
            HostActionEntryRequestContext,
            CancellationToken,
            ValueTask<OutOfProcessCrossSidecarDispatchResult>> DispatchCrossSidecar);
}

internal sealed record OutOfProcessActionDispatchResult(
    ActionOutcomeKind Kind,
    object? Result,
    ExecutionError? Error,
    ActionUncertainty? Uncertainty,
    ContinuationToken? Continuation,
    int TerminalCallCount);

internal sealed record OutOfProcessCrossSidecarDispatchResult(
    ActionOutcomeKind Kind,
    SidecarSerializedPayload? Result,
    ExecutionError? Error,
    ActionUncertainty? Uncertainty,
    ContinuationToken? Continuation,
    SidecarActionTerminalTransportResponse? TerminalResponse);

/// <summary>Supplies the exact host services used by one authorized sidecar session.</summary>
public sealed class OutOfProcessCapabilityHostOptions
{
    /// <summary>Creates one explicit capability host binding.</summary>
    public OutOfProcessCapabilityHostOptions(
        IScopedStorageGateway storageGateway,
        IActionDispatcher actionDispatcher,
        SidecarCapabilityGrant grant,
        IEnumerable<string> ownedStorageNames,
        OutOfProcessActionDescriptorCatalog actionDescriptors,
        ActionPipelineSnapshot actionSnapshot,
        OutOfProcessHostActionEntryContextRegistry hostActionEntryContexts,
        KernelExternalAuthoritySessionRegistry externalAuthorityRegistry,
        OutOfProcessCrossSidecarActionEntryCatalog? crossSidecarActionEntries = null)
    {
        StorageGateway = storageGateway
            ?? throw new ArgumentNullException(nameof(storageGateway));
        ActionDispatcher = actionDispatcher
            ?? throw new ArgumentNullException(nameof(actionDispatcher));
        Grant = grant ?? throw new ArgumentNullException(nameof(grant));
        ActionDescriptors = actionDescriptors
            ?? throw new ArgumentNullException(nameof(actionDescriptors));
        ActionSnapshot = actionSnapshot
            ?? throw new ArgumentNullException(nameof(actionSnapshot));
        HostActionEntryContexts = hostActionEntryContexts
            ?? throw new ArgumentNullException(nameof(hostActionEntryContexts));
        ExternalAuthorityRegistry = externalAuthorityRegistry
            ?? throw new ArgumentNullException(nameof(externalAuthorityRegistry));
        CrossSidecarActionEntries = crossSidecarActionEntries;
        ArgumentNullException.ThrowIfNull(ownedStorageNames);
        OwnedStorageNames = new HashSet<string>(
            ownedStorageNames.Where(value => !string.IsNullOrWhiteSpace(value)),
            StringComparer.Ordinal);
    }

    /// <summary>Gets the exact host storage gateway singleton.</summary>
    public IScopedStorageGateway StorageGateway { get; }

    /// <summary>Gets the exact host action dispatcher singleton.</summary>
    public IActionDispatcher ActionDispatcher { get; }

    /// <summary>Gets the explicit capability grant sent to the sidecar.</summary>
    public SidecarCapabilityGrant Grant { get; }

    /// <summary>Gets the module-owned storage names allowed by this session.</summary>
    public IReadOnlySet<string> OwnedStorageNames { get; }

    /// <summary>Gets the typed host action descriptor lookup.</summary>
    public OutOfProcessActionDescriptorCatalog ActionDescriptors { get; }

    /// <summary>Gets the host-owned action pipeline snapshot used for every dispatch.</summary>
    public ActionPipelineSnapshot ActionSnapshot { get; }

    /// <summary>Gets the one-use host context registry for ingress carriers.</summary>
    public OutOfProcessHostActionEntryContextRegistry HostActionEntryContexts { get; }

    internal KernelExternalAuthoritySessionRegistry ExternalAuthorityRegistry { get; }

    /// <summary>Gets the host-owned target action-entry catalog.</summary>
    public OutOfProcessCrossSidecarActionEntryCatalog? CrossSidecarActionEntries { get; }

    internal Func<Task>? BeforeRotationStartAsync { get; set; }

    internal Func<Task>? BeforeCarrierSessionBeginAsync { get; set; }
}

/// <summary>Creates the capability grant shared by one authorized host and sidecar.</summary>
public static class OutOfProcessCapabilityGrantFactory
{
    /// <summary>Creates a grant bound to the discovered module graph and host authorization.</summary>
    public static SidecarCapabilityGrant Create(
        SidecarDiscoveryEnvelope discovery,
        SidecarHostAuthorization authorization,
        DateTimeOffset? expiresAt = null)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        ArgumentNullException.ThrowIfNull(authorization);
        if (!string.Equals(discovery.SourceId, authorization.SourceId, StringComparison.Ordinal))
            throw new ArgumentException(
                "The discovery and authorization module identities do not match.",
                nameof(authorization));

        var issuedAt = DateTimeOffset.UtcNow;
        var expiry = expiresAt ?? issuedAt.AddMinutes(5);
        if (expiry <= issuedAt)
            throw new ArgumentOutOfRangeException(nameof(expiresAt));

        return new SidecarCapabilityGrant(
            Guid.NewGuid().ToString("N"),
            discovery.SourceId,
            discovery.ContractHash,
            [SidecarCapabilityKind.Action, SidecarCapabilityKind.Storage],
            OutOfProcessCapabilitySecurity.ComputeAuthorizationHash(authorization),
            issuedAt,
            expiry);
    }
}
