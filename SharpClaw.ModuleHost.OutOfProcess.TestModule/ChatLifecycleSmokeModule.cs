using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Providers;

namespace SharpClaw.ModuleHost.OutOfProcess.TestModule;

public sealed class ChatLifecycleSmokeModule : ISharpClawModule
{
    public ModuleIdentity Identity { get; } = new(
        "chat_lifecycle_smoke",
        "Chat Lifecycle Smoke",
        "chat_smoke");

    public void Configure(ISharpClawModuleBuilder module)
    {
        module.Services.AddScoped<SmokeConversationResolver>();
        module.Services.AddScoped<IConversationResolver>(services =>
            services.GetRequiredService<SmokeConversationResolver>());
        module.Services.AddScoped<SmokeProfileResolver>();
        module.Services.AddScoped<IChatProfileResolver>(services =>
            services.GetRequiredService<SmokeProfileResolver>());
        module.Services.AddScoped<SmokeConversationStore>();
        module.Services.AddScoped<IConversationStore>(services =>
            services.GetRequiredService<SmokeConversationStore>());
        module.Services.AddScoped<SmokeContextContributor>();
        module.Services.AddScoped<IChatContextContributor>(services =>
            services.GetRequiredService<SmokeContextContributor>());
        module.Chat.UseConversationResolver<SmokeConversationResolver>(
            new ExclusiveRegistration("chat-smoke-conversation"));
        module.Chat.UseChatProfileResolver<SmokeProfileResolver>(
            new ExclusiveRegistration("chat-smoke-profile"));
        module.Chat.AddContextContributor<SmokeContextContributor>();
    }

    public ValueTask StartAsync(ModuleStartContext context, CancellationToken ct) =>
        ValueTask.CompletedTask;

    public ValueTask StopAsync(CancellationToken ct) => ValueTask.CompletedTask;
}

public sealed class SmokeConversationResolver : IConversationResolver
{
    public ValueTask<ConversationSelection> ResolveAsync(
        ChatTurnInput input,
        ChatOperationContext context,
        CancellationToken ct) =>
        ValueTask.FromResult(new ConversationSelection(
            input.ConversationId ?? Guid.Parse("52c442f1-cc2d-40c6-8462-6bd2ff2863fb"),
            input.ConversationId is null));
}

public sealed class SmokeProfileResolver : IChatProfileResolver
{
    public ValueTask<ChatProfile> ResolveAsync(
        ChatTurnContext turn,
        ChatOperationContext context,
        CancellationToken ct) =>
        ValueTask.FromResult(new ChatProfile(
            "smoke-provider",
            Guid.Parse("0ca1da69-ec35-4f6e-a2f7-71df88179899"),
            "smoke-model"));
}

public sealed class SmokeConversationStore : IConversationStore
{
    public ValueTask<IReadOnlyList<ChatCompletionMessage>> LoadHistoryAsync(
        Guid conversationId,
        ChatOperationContext context,
        CancellationToken ct) =>
        ValueTask.FromResult<IReadOnlyList<ChatCompletionMessage>>([]);

    public ValueTask CommitExchangeAsync(
        ChatExchange exchange,
        ChatOperationContext context,
        CancellationToken ct) =>
        ValueTask.CompletedTask;
}

public sealed class SmokeContextContributor : IChatContextContributor
{
    public ValueTask<ChatContextContribution> ContributeAsync(
        ChatContextRequest request,
        ChatOperationContext context,
        CancellationToken ct) =>
        ValueTask.FromResult(ChatContextContribution.Empty);
}
