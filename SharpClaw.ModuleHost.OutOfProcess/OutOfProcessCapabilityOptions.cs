using System.Security.Cryptography;
using System.Text;
using SharpClaw.Contracts.Modules;

namespace SharpClaw.ModuleHost.OutOfProcess;

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
            ValueTask<OutOfProcessActionDispatchResult>> Dispatch);
}

internal sealed record OutOfProcessActionDispatchResult(
    ActionOutcomeKind Kind,
    object? Result,
    ExecutionError? Error,
    ActionUncertainty? Uncertainty,
    ContinuationToken? Continuation,
    int TerminalCallCount);

/// <summary>Supplies the exact host services used by one authorized sidecar session.</summary>
public sealed class OutOfProcessCapabilityHostOptions
{
    /// <summary>Creates one explicit capability host binding.</summary>
    public OutOfProcessCapabilityHostOptions(
        IModuleStorageGateway storageGateway,
        IActionDispatcher actionDispatcher,
        SidecarCapabilityGrant grant,
        IEnumerable<string> ownedStorageNames,
        OutOfProcessActionDescriptorCatalog actionDescriptors,
        ActionPipelineSnapshot actionSnapshot,
        OutOfProcessHostActionEntryContextRegistry hostActionEntryContexts)
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
        ArgumentNullException.ThrowIfNull(ownedStorageNames);
        OwnedStorageNames = new HashSet<string>(
            ownedStorageNames.Where(value => !string.IsNullOrWhiteSpace(value)),
            StringComparer.Ordinal);
        if (OwnedStorageNames.Count == 0)
            throw new ArgumentException(
                "At least one owned storage name is required.",
                nameof(ownedStorageNames));
    }

    /// <summary>Gets the exact host storage gateway singleton.</summary>
    public IModuleStorageGateway StorageGateway { get; }

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
        if (!string.Equals(discovery.ModuleId, authorization.ModuleId, StringComparison.Ordinal))
            throw new ArgumentException(
                "The discovery and authorization module identities do not match.",
                nameof(authorization));

        var issuedAt = DateTimeOffset.UtcNow;
        var expiry = expiresAt ?? issuedAt.AddMinutes(5);
        if (expiry <= issuedAt)
            throw new ArgumentOutOfRangeException(nameof(expiresAt));

        return new SidecarCapabilityGrant(
            Guid.NewGuid().ToString("N"),
            discovery.ModuleId,
            discovery.ContractHash,
            [SidecarCapabilityKind.Action, SidecarCapabilityKind.Storage],
            OutOfProcessCapabilitySecurity.ComputeAuthorizationHash(authorization),
            issuedAt,
            expiry);
    }
}
