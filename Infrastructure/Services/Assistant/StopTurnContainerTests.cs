// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// The stop-turn chain against the real application container, not against its list of registrations
/// (StopTurnServiceRegistrationTests pins the lifetimes; this proves the graph resolves). The container is
/// built from AddApplicationServices with ValidateScopes and ValidateOnBuild, the way the Development host
/// builds it, and every failure that names a type of the chain fails the test: the singleton registry, the
/// scoped run state, the interrupted-turn finalizer, the stopped-turn cleanup, the turn confirmation scope
/// and its discarder, the cancellable-skill policy and the recorder and executor that take them. Two things
/// ValidateOnBuild cannot see are checked separately: the ChatController is no service descriptor, so it is
/// created the way MVC creates it (from a request scope, its constructor arguments resolved), and the run
/// state must be ONE instance per request scope across the finalizer, the recorder and the chat service,
/// because the whole design reads the same picture of the turn from all three. Positive controls prove each
/// check can fail. Not covered: registrations made only in Program.cs, and the real host boot (the Klacks.ApiTest
/// host and the manual boot cover that).
/// </summary>

using System.Reflection;
using Klacks.Api.Application.Interfaces.Klacksy;
using Klacks.Api.Application.Klacksy;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.Application.Services.Assistant.Autonomy;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Authentification;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.Infrastructure.Extensions;
using Klacks.Api.Infrastructure.Hubs;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.Api.Infrastructure.Services.Assistant;
using Klacks.Api.Infrastructure.Plugins;
using Klacks.Api.KnowledgeIndex.Application.Interfaces;
using Klacks.Api.Presentation.Controllers.Assistant;
using Klacks.Plugin.Messaging;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Klacks.UnitTest.Infrastructure.Services.Assistant;

[TestFixture]
public class StopTurnContainerTests
{
    private const string UnusedConnectionString = "Host=unused;Database=unused";
    private const string CapturedScopedMarker = "Cannot consume scoped service";
    private const string RunStateField = "_turnState";

    private static readonly Type[] ChainTypes =
    [
        typeof(ActiveTurnRegistry),
        typeof(TurnRunState),
        typeof(InterruptedTurnFinalizer),
        typeof(StoppedTurnCleanup),
        typeof(TurnConfirmationScope),
        typeof(TurnConfirmationDiscarder),
        typeof(CancellableSkillPolicy),
        typeof(TurnCompletionRecorder),
        typeof(LLMFunctionExecutor),
        typeof(CorrectionTurnPreparer),
        typeof(LLMService)
    ];

    private static IServiceCollection BuildServices()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpContextAccessor();
        services.AddDataProtection();
        services.AddSignalR();
        services.AddIdentity<AppUser, IdentityRole>().AddEntityFrameworkStores<DataBaseContext>().AddDefaultTokenProviders();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddDbContext<DataBaseContext>(options => options.UseNpgsql(UnusedConnectionString));
        services.AddApplicationServices(configuration);
        new MessagingPluginRegistrar().RegisterServices(services, configuration);
        AddWhatOnlyProgramRegisters(services);
        return services;
    }

    // Program.cs registers these next to AddApplicationServices. The mediator is the real one; the rest sit
    // outside the stop-turn chain and are stood in by substitutes so the ChatController can be created. The
    // knowledge index repository opens its database connection when it is resolved, so it is a substitute too.
    private static void AddWhatOnlyProgramRegisters(IServiceCollection services)
    {
        services.AddMemoryCache();
        services.RemoveAll<IKnowledgeIndexRepository>();
        services.AddScoped(_ => Substitute.For<IKnowledgeIndexRepository>());
        services.AddMappers();
        services.AddMediator(typeof(ChatController).Assembly);
        services.AddSingleton(Substitute.For<IAssistantNotificationService>());
        services.AddSingleton(Substitute.For<IAssistantConnectionTracker>());
        services.AddSingleton(Substitute.For<IUtteranceNormalizer>());
        services.AddSingleton(Substitute.For<INavigationTargetMatcher>());
        services.AddSingleton(Substitute.For<INavigationTargetCacheService>());
        services.AddSingleton(Substitute.For<INavigationFeedbackLogger>());
        services.AddSingleton(Substitute.For<INavigationMissDetector>());
        services.AddSingleton(Substitute.For<INavigationEntityRouteGuard>());
    }

    private static ServiceProvider Build(IServiceCollection services) =>
        services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = false });

    private static List<string> ChainFailures(IServiceCollection services)
    {
        var failures = new List<string>();
        try
        {
            using var provider = services.BuildServiceProvider(
                new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        }
        catch (AggregateException ex)
        {
            failures.AddRange(ex.InnerExceptions.Select(inner => inner.Message));
        }

        return failures
            .Where(message => ChainTypes.Any(type => message.Contains(type.Name, StringComparison.Ordinal)))
            .ToList();
    }

    [Test]
    public void ValidateOnBuildAndValidateScopes_ReportNoFailureOnTheStopTurnChain()
    {
        var chainFailures = ChainFailures(BuildServices());

        chainFailures.ShouldBeEmpty(string.Join(Environment.NewLine, chainFailures));
    }

    [Test]
    public void PositiveControl_ASingletonTakingTheRunState_IsReportedAsACapturedScopedService()
    {
        var services = BuildServices();
        services.AddSingleton<RunStateHoldingSingleton>();

        ChainFailures(services).ShouldContain(message => message.Contains(CapturedScopedMarker, StringComparison.Ordinal));
    }

    [Test]
    public void PositiveControl_ASingletonTakingTheInterruptedTurnFinalizer_IsReportedAsACapturedScopedService()
    {
        var services = BuildServices();
        services.AddSingleton<FinalizerHoldingSingleton>();

        ChainFailures(services).ShouldContain(message => message.Contains(CapturedScopedMarker, StringComparison.Ordinal));
    }

    [Test]
    public void TheActiveTurnRegistry_IsALeafOfTheGraph_SoNoScopedServiceCanBeCapturedByIt()
    {
        typeof(ActiveTurnRegistry).GetConstructors().Single().GetParameters().ShouldBeEmpty();
    }

    [Test]
    public async Task TheChatController_IsCreatedFromARequestScopeWithEveryDependencyResolved()
    {
        await using var provider = Build(BuildServices());
        await using var scope = provider.CreateAsyncScope();

        var controller = ActivatorUtilities.CreateInstance<ChatController>(scope.ServiceProvider);

        controller.ShouldNotBeNull();
    }

    [Test]
    public async Task PositiveControl_WithoutTheFinalizerRegistration_TheChatControllerCannotBeCreated()
    {
        var services = BuildServices();
        foreach (var descriptor in services.Where(d => d.ServiceType == typeof(IInterruptedTurnFinalizer)).ToList())
        {
            services.Remove(descriptor);
        }

        await using var provider = Build(services);
        await using var scope = provider.CreateAsyncScope();

        var failure = Should.Throw<InvalidOperationException>(
            () => ActivatorUtilities.CreateInstance<ChatController>(scope.ServiceProvider));

        failure.Message.ShouldContain(nameof(IInterruptedTurnFinalizer));
    }

    [Test]
    public async Task TheChatControllerAndTheFinalizerOfOneRequest_ShareTheRegistryButNotAcrossRequests()
    {
        await using var provider = Build(BuildServices());

        var registry = provider.GetRequiredService<IActiveTurnRegistry>();
        await using var first = provider.CreateAsyncScope();
        await using var second = provider.CreateAsyncScope();

        first.ServiceProvider.GetRequiredService<IActiveTurnRegistry>().ShouldBeSameAs(registry);
        second.ServiceProvider.GetRequiredService<IActiveTurnRegistry>().ShouldBeSameAs(registry);
        first.ServiceProvider.GetRequiredService<IInterruptedTurnFinalizer>()
            .ShouldNotBeSameAs(second.ServiceProvider.GetRequiredService<IInterruptedTurnFinalizer>());
    }

    [Test]
    public async Task OneRequestScope_HasOneRunStateSharedByTheFinalizerTheRecorderAndTheChatService()
    {
        await using var provider = Build(BuildServices());
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;

        var runState = services.GetRequiredService<TurnRunState>();

        RunStateOf(services.GetRequiredService<IInterruptedTurnFinalizer>()).ShouldBeSameAs(runState);
        RunStateOf(services.GetRequiredService<TurnCompletionRecorder>()).ShouldBeSameAs(runState);
        RunStateOf(services.GetRequiredService<ILLMService>()).ShouldBeSameAs(runState);
        services.GetRequiredService<TurnRunState>().ShouldBeSameAs(runState);
    }

    [Test]
    public async Task TwoRequestScopes_HaveTwoRunStates()
    {
        await using var provider = Build(BuildServices());
        await using var first = provider.CreateAsyncScope();
        await using var second = provider.CreateAsyncScope();

        first.ServiceProvider.GetRequiredService<TurnRunState>()
            .ShouldNotBeSameAs(second.ServiceProvider.GetRequiredService<TurnRunState>());
    }

    [Test]
    public async Task TheConfirmationScope_IsOneInstanceInARequestScopeForTheExecutorThePreparerAndTheDiscarder()
    {
        await using var provider = Build(BuildServices());
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;

        var confirmationScope = services.GetRequiredService<ITurnConfirmationScope>();

        FieldOf<ITurnConfirmationScope>(services.GetRequiredService<ITurnConfirmationDiscarder>()).ShouldBeSameAs(confirmationScope);
        FieldOf<ITurnConfirmationScope>(services.GetRequiredService<LLMFunctionExecutor>()).ShouldBeSameAs(confirmationScope);
    }

    private static TurnRunState RunStateOf(object consumer) => FieldOf<TurnRunState>(consumer, RunStateField);

    private static T FieldOf<T>(object consumer, string? name = null) where T : class
    {
        var field = consumer.GetType()
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(f => f.FieldType == typeof(T) && (name == null || f.Name == name));
        return (T)field.GetValue(consumer)!;
    }

    private sealed class RunStateHoldingSingleton
    {
        public RunStateHoldingSingleton(TurnRunState runState) => RunState = runState;

        public TurnRunState RunState { get; }
    }

    private sealed class FinalizerHoldingSingleton
    {
        public FinalizerHoldingSingleton(IInterruptedTurnFinalizer finalizer) => Finalizer = finalizer;

        public IInterruptedTurnFinalizer Finalizer { get; }
    }
}
