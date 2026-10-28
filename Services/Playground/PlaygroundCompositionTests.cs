// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Composition test of the Playground wiring with the project's own registration extension methods (no hand-built
/// graph): in Playground mode the container resolves the decorators of the background tasks and the navigation feedback,
/// the model pin, the conversation store, the provider factory and the budget, and the services that depend on them really
/// use them. A decorator that is not registered would leave the Playground silently unprotected without any build error.
/// With the mode off the same container behaves as on a customer installation.
/// </summary>

using Klacks.Api.Application.Interfaces.Klacksy;
using Klacks.Api.Application.Services.Playground;
using Klacks.Api.Domain.Interfaces.Playground;
using Klacks.Api.Domain.Models.Klacksy;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.Infrastructure.Extensions;
using Klacks.Api.Infrastructure.Services.Assistant;
using Klacks.Api.Application.Constants;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Services.Playground;

[TestFixture]
public class PlaygroundCompositionTests
{
    private const string UserId = "demo-admin";
    private const string ClientModel = "gpt-expensive";

    private ILLMRepository _repository = null!;
    private IKlacksyNavigationFeedbackRepository _feedbackRepository = null!;

    private ServiceProvider Build(bool playground)
    {
        _repository = Substitute.For<ILLMRepository>();
        _feedbackRepository = Substitute.For<IKlacksyNavigationFeedbackRepository>();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [DeploymentConstants.IsPlaygroundConfigKey] = playground ? "true" : "false",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddPlaygroundTokenLimit(configuration);
        services.AddPlaygroundNavigationFeedbackLogger();
        services.AddLLMCoreServices();
        services.AddScoped(_ => _repository);
        services.AddScoped(_ => _feedbackRepository);

        return services.BuildServiceProvider();
    }

    [Test]
    public void ModeOn_BackgroundTasksAndNavigationFeedback_AreTheSilentDecorators()
    {
        using var provider = Build(playground: true);
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<ILLMBackgroundTaskService>().ShouldBeOfType<PlaygroundSilentBackgroundTaskService>();
        scope.ServiceProvider.GetRequiredService<INavigationFeedbackLogger>().ShouldBeOfType<PlaygroundSilentNavigationFeedbackLogger>();
    }

    [Test]
    public async Task ModeOn_NavigationFeedback_NeverReachesTheRepository()
    {
        using var provider = Build(playground: true);
        using var scope = provider.CreateScope();
        var logger = scope.ServiceProvider.GetRequiredService<INavigationFeedbackLogger>();

        await logger.LogAsync("go to the schedule", "en", null, 0, null, Guid.NewGuid(), CancellationToken.None);

        _feedbackRepository.ReceivedCalls().ShouldBeEmpty();
    }

    [Test]
    public async Task ModeOff_NavigationFeedback_IsForwardedToTheRepository()
    {
        using var provider = Build(playground: false);
        using var scope = provider.CreateScope();
        var logger = scope.ServiceProvider.GetRequiredService<INavigationFeedbackLogger>();

        await logger.LogAsync("go to the schedule", "en", null, 0, null, Guid.NewGuid(), CancellationToken.None);

        await _feedbackRepository.Received(1).AddAsync(Arg.Any<KlacksyNavigationFeedback>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public void ModeOn_PinAndStoreAreActive()
    {
        using var provider = Build(playground: true);
        using var scope = provider.CreateScope();

        var pin = scope.ServiceProvider.GetRequiredService<IPlaygroundModelPin>();
        var store = scope.ServiceProvider.GetRequiredService<IPlaygroundConversationStore>();

        pin.IsPinned.ShouldBeTrue();
        pin.ResolveChatModelId(ClientModel).ShouldNotBe(ClientModel);
        store.IsActive.ShouldBeTrue();
        scope.ServiceProvider.GetRequiredService<IPlaygroundMode>().IsActive.ShouldBeTrue();
    }

    [Test]
    public void ModeOff_PinAndStoreAreInert()
    {
        using var provider = Build(playground: false);
        using var scope = provider.CreateScope();

        var pin = scope.ServiceProvider.GetRequiredService<IPlaygroundModelPin>();

        pin.IsPinned.ShouldBeFalse();
        pin.ResolveChatModelId(ClientModel).ShouldBe(ClientModel);
        scope.ServiceProvider.GetRequiredService<IPlaygroundConversationStore>().IsActive.ShouldBeFalse();
    }

    [Test]
    public async Task ModeOn_Orchestrator_ResolvesThePinnedModelWhateverTheClientAsks()
    {
        using var provider = Build(playground: true);
        using var scope = provider.CreateScope();
        var orchestrator = scope.ServiceProvider.GetRequiredService<LLMProviderOrchestrator>();
        var pinned = scope.ServiceProvider.GetRequiredService<IPlaygroundModelPin>().ResolveChatModelId(ClientModel)!;

        await orchestrator.GetModelAndProviderAsync(ClientModel);

        await _repository.Received().GetModelByIdAsync(pinned);
        await _repository.DidNotReceive().GetModelByIdAsync(ClientModel);
    }

    [Test]
    public async Task ModeOff_Orchestrator_ResolvesTheModelTheClientAsksFor()
    {
        using var provider = Build(playground: false);
        using var scope = provider.CreateScope();
        var orchestrator = scope.ServiceProvider.GetRequiredService<LLMProviderOrchestrator>();

        await orchestrator.GetModelAndProviderAsync(ClientModel);

        await _repository.Received().GetModelByIdAsync(ClientModel);
    }

    [Test]
    public async Task ModeOn_ConversationManager_KeepsTheConversationOutOfTheRepository()
    {
        using var provider = Build(playground: true);
        using var scope = provider.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<LLMConversationManager>();

        var conversation = await manager.GetOrCreateConversationAsync("conv-1", UserId);

        conversation.ShouldNotBeNull();
        await _repository.DidNotReceiveWithAnyArgs().GetOrCreateConversationAsync(default!, default!);
    }

    [Test]
    public async Task ModeOff_ConversationManager_UsesTheRepository()
    {
        using var provider = Build(playground: false);
        using var scope = provider.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<LLMConversationManager>();

        await manager.GetOrCreateConversationAsync("conv-1", UserId);

        await _repository.Received(1).GetOrCreateConversationAsync("conv-1", UserId);
    }

    [Test]
    public void ProviderFactory_AndBudget_AreResolvable()
    {
        using var provider = Build(playground: true);
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<ILLMProviderFactory>().ShouldBeOfType<LLMProviderFactory>();
        scope.ServiceProvider.GetRequiredService<IPlaygroundTokenBudget>().ShouldBeOfType<PlaygroundTokenBudget>();
    }

    [Test]
    public void Budget_IsOneSharedInstanceAcrossScopes()
    {
        using var provider = Build(playground: true);
        using var first = provider.CreateScope();
        using var second = provider.CreateScope();

        first.ServiceProvider.GetRequiredService<IPlaygroundTokenBudget>()
            .ShouldBeSameAs(second.ServiceProvider.GetRequiredService<IPlaygroundTokenBudget>());
    }
}
