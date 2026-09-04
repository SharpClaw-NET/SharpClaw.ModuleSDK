using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SharpClaw.Contracts.Kernel;

namespace SharpClaw.ModuleSDK;

/// <summary>Defines the neutral authorization action and contract identities.</summary>
public static class AuthorizationProtocol
{
    public const string ContractName = "sharpclaw.authorization";

    internal const string RestrictionFailureCodePrefix = "authorization_restricted:";

    public static readonly Guid TerminalId =
        Guid.Parse("4c6e9795-c0bd-5f63-9392-fbdb1f1cf9e6");

    public static readonly ActionDescriptor<AuthorizationRequest, AuthorizationDecision> Evaluate =
        CreateDescriptor();

    private static ActionDescriptor<AuthorizationRequest, AuthorizationDecision> CreateDescriptor()
    {
        var key = new SharpClawActionKey("authorization.evaluate");
        return new ActionDescriptor<AuthorizationRequest, AuthorizationDecision>(
            key,
            1,
            "authorization",
            ActionInterceptionCapabilities.Inspect |
            ActionInterceptionCapabilities.Wrap |
            ActionInterceptionCapabilities.Observe,
            true,
            false,
            new ActionRepeatPolicy(
                ActionRepeatKind.Idempotent,
                3,
                TimeSpan.FromMilliseconds(50),
                "authorization"),
            null,
            TimeSpan.FromSeconds(10))
        {
            InputSchema = ModuleSchemaIdentity.ActionInput(key, 1, typeof(AuthorizationRequest)),
            ResultSchema = ModuleSchemaIdentity.ActionResult(key, 1, typeof(AuthorizationDecision)),
            SafePoints =
            [
                ActionSafePoint.BeforeTerminal,
                ActionSafePoint.AfterTerminal,
            ],
        };
    }
}

/// <summary>Adds neutral authorization services to a discovered package.</summary>
public static class AuthorizationBuilderExtensions
{
    private const ActionInterceptionCapabilities RestrictionCapabilities =
        ActionInterceptionCapabilities.Inspect |
        ActionInterceptionCapabilities.Wrap;

    /// <summary>Adds the single authoritative policy provider.</summary>
    public static void AddAuthorizationPolicy<TPolicy>(this IServiceCollection services)
        where TPolicy : class, IAuthorizationPolicy
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddScoped<TPolicy>();
        services.TryAddScoped<IAuthorizationPolicy>(provider =>
            provider.GetRequiredService<TPolicy>());
        services.ExportContract<AuthorizationContract>(AuthorizationProtocol.ContractName);
        services.AddAction(AuthorizationProtocol.Evaluate)
            .UseTerminal<AuthorizationPolicyTerminal>(AuthorizationProtocol.TerminalId);
    }

    /// <summary>Adds access to the active authorization provider.</summary>
    public static void RequireAuthorization(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddScoped<HostAuthorizationEntry>();
        services.RequireContract<AuthorizationContract>(AuthorizationProtocol.ContractName);
    }

    /// <summary>Adds one independent restriction that can preserve or deny access.</summary>
    public static void AddAuthorizationRestriction<TRestriction>(
        this IServiceCollection services,
        string restrictionId,
        HookPriority priority = HookPriority.Normal)
        where TRestriction : class, IAuthorizationRestriction
    {
        ArgumentNullException.ThrowIfNull(services);
        ValidateRestrictionId(restrictionId);
        services.TryAddScoped<TRestriction>();
        services.RequireContract<AuthorizationContract>(AuthorizationProtocol.ContractName);
        services.OnAction(AuthorizationProtocol.Evaluate)
            .Use<AuthorizationRestrictionHook<TRestriction>>(
                RestrictionCapabilities,
                new HookOrdering($"authorization.restriction.{restrictionId}", priority));
        services.TryAddScoped<AuthorizationRestrictionHook<TRestriction>>();
    }

    private static void ValidateRestrictionId(string restrictionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(restrictionId);
        if (restrictionId.Length > 80 ||
            !restrictionId.All(character =>
                character is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-' or '_'))
        {
            throw new ArgumentException(
                "A restriction identifier must use lowercase ASCII letters, digits, periods, hyphens, or underscores.",
                nameof(restrictionId));
        }
    }
}

/// <summary>Runs the active authorization policy.</summary>
public sealed class AuthorizationPolicyTerminal(IAuthorizationPolicy policy)
    : IHostActionEntryTerminal<AuthorizationRequest, AuthorizationDecision>
{
    public Guid TerminalId => AuthorizationProtocol.TerminalId;

    public ValueTask<AuthorizationDecision> InvokeAsync(
        ActionContext<AuthorizationRequest> context,
        CancellationToken cancellationToken = default)
    {
        context.Action.Validate();
        return policy.EvaluateAsync(context, cancellationToken);
    }
}

/// <summary>Applies one restriction before the authoritative policy runs.</summary>
public sealed class AuthorizationRestrictionHook<TRestriction>(TRestriction restriction)
    : IActionInterceptor<AuthorizationRequest, AuthorizationDecision>
    where TRestriction : class, IAuthorizationRestriction
{
    public async ValueTask<IActionOutcome<AuthorizationDecision>> InvokeAsync(
        ActionContext<AuthorizationRequest> context,
        IActionControl<AuthorizationRequest, AuthorizationDecision> control,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await restriction.EvaluateAsync(context, cancellationToken);
        if (!result.Denied)
            return await control.ProceedAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(result.Code) || string.IsNullOrWhiteSpace(result.Message))
        {
            return control.Fail(new ExecutionError(
                "authorization_restriction_invalid",
                "An authorization restriction returned an invalid decision."));
        }

        return control.Fail(new ExecutionError(
            AuthorizationProtocol.RestrictionFailureCodePrefix + result.Code,
            result.Message));
    }
}

/// <summary>Evaluates authorization through the host-owned action entry.</summary>
public sealed class HostAuthorizationEntry(IHostActionEntry host)
{
    public ValueTask<AuthorizationDecision> EvaluateAsync(
        HostActionEntryRequestContext hostContext,
        AuthorizationRequest request,
        CancellationToken cancellationToken = default) =>
        InvokeAsync(host, hostContext, request, cancellationToken);

    public ValueTask<AuthorizationDecision> EvaluateAsync<TParentAction>(
        ActionContext<TParentAction> parentContext,
        AuthorizationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parentContext);
        var hostEntry = parentContext.HostActionEntry
            ?? throw new InvalidOperationException("The parent action has no host action entry.");
        return InvokeNestedAsync(hostEntry, parentContext, request, cancellationToken);
    }

    public ValueTask<AuthorizationDecision> EvaluateAsync(
        ChatOperationContext context,
        AuthorizationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var hostEntry = context.HostActionEntry
            ?? throw new InvalidOperationException("The chat operation has no host action entry.");
        return InvokeCrossSidecarAsync(hostEntry, request, cancellationToken);
    }

    private static async ValueTask<AuthorizationDecision> InvokeAsync(
        IHostActionEntry hostEntry,
        HostActionEntryRequestContext hostContext,
        AuthorizationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(hostContext);
        request.Validate();
        var outcome = await hostEntry.InvokeCrossSidecarAsync(
            new ModuleCrossSidecarActionEntryRequest<AuthorizationRequest, AuthorizationDecision>(
                AuthorizationProtocol.Evaluate,
                request),
            cancellationToken);
        return RequireResult(outcome, cancellationToken);
    }

    private static async ValueTask<AuthorizationDecision> InvokeNestedAsync<TParentAction>(
        IHostActionEntry hostEntry,
        ActionContext<TParentAction> parentContext,
        AuthorizationRequest request,
        CancellationToken cancellationToken)
    {
        request.Validate();
        var outcome = await hostEntry.InvokeCrossSidecarAsync(
            new ModuleCrossSidecarActionEntryRequest<AuthorizationRequest, AuthorizationDecision>(
                AuthorizationProtocol.Evaluate,
                request),
            cancellationToken);
        return RequireResult(outcome, cancellationToken);
    }

    private static async ValueTask<AuthorizationDecision> InvokeCrossSidecarAsync(
        IHostActionEntry hostEntry,
        AuthorizationRequest request,
        CancellationToken cancellationToken)
    {
        request.Validate();
        var outcome = await hostEntry.InvokeCrossSidecarAsync(
            new ModuleCrossSidecarActionEntryRequest<AuthorizationRequest, AuthorizationDecision>(
                AuthorizationProtocol.Evaluate,
                request),
            cancellationToken);
        return RequireResult(outcome, cancellationToken);
    }

    private static AuthorizationDecision RequireResult(
        IActionOutcome<AuthorizationDecision> outcome,
        CancellationToken cancellationToken)
    {
        if (outcome.Kind == ActionOutcomeKind.Failed &&
            outcome.Error is { } error &&
            error.Code.StartsWith(
                AuthorizationProtocol.RestrictionFailureCodePrefix,
                StringComparison.Ordinal))
        {
            var code = error.Code[AuthorizationProtocol.RestrictionFailureCodePrefix.Length..];
            return AuthorizationDecision.Deny(code, error.Message);
        }

        return outcome.Kind switch
        {
            ActionOutcomeKind.Completed => outcome.Result
                ?? throw new InvalidOperationException("Authorization completed without a decision."),
            ActionOutcomeKind.Cancelled => throw new OperationCanceledException(
                "Authorization was cancelled.",
                cancellationToken),
            ActionOutcomeKind.Deferred => throw new InvalidOperationException("Authorization was deferred."),
            ActionOutcomeKind.Failed => throw new InvalidOperationException(FormatFailure(outcome.Error)),
            ActionOutcomeKind.Uncertain => throw new InvalidOperationException("Authorization has uncertain execution."),
            _ => throw new InvalidOperationException("Authorization returned an unknown outcome."),
        };
    }

    private static string FormatFailure(ExecutionError? error) =>
        error is null
            ? "Authorization failed without an error."
            : $"Authorization failed: {error.Code}: {error.Message}";
}

/// <summary>Provides request-scoped authorization without exposing transport details.</summary>
public interface IAuthorizationClient
{
    RequestPrincipal Caller { get; }

    ValueTask<AuthorizationDecision> EvaluateAsync(
        AuthorizationRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>Binds authorization to one active action context.</summary>
public sealed class ActionAuthorizationClient<TAction>(
    ActionContext<TAction> context,
    HostAuthorizationEntry authorization) : IAuthorizationClient
{
    public RequestPrincipal Caller => context.Caller;

    public ValueTask<AuthorizationDecision> EvaluateAsync(
        AuthorizationRequest request,
        CancellationToken cancellationToken = default) =>
        authorization.EvaluateAsync(context, request, cancellationToken);
}

/// <summary>Binds authorization to one active chat operation.</summary>
public sealed class ChatAuthorizationClient(
    ChatOperationContext context,
    HostAuthorizationEntry authorization) : IAuthorizationClient
{
    public RequestPrincipal Caller => context.Caller;

    public ValueTask<AuthorizationDecision> EvaluateAsync(
        AuthorizationRequest request,
        CancellationToken cancellationToken = default) =>
        authorization.EvaluateAsync(context, request, cancellationToken);
}
