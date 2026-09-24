// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Builds the real application container (AddApplicationServices plus the messaging plugin registrar)
/// and checks the wiring of the ClarificationCoordinator. The channel adapters (EmailPollingBackgroundService,
/// MessengerIntentProcessor) resolve the coordinator lazily through ClarificationDialogSafeGuard, which turns
/// every resolution failure into "no dialog"; a broken registration would therefore never show up as a
/// startup or test failure, only as a silently dead feature. These tests close that gap: the coordinator and
/// both reply senders are registered, ValidateOnBuild reports no failure naming the coordinator or any type
/// on its constructor chain, the coordinator can actually be created from a scope, and a positive control
/// proves the check sees a removed dependency. The framework registrations Program.cs adds outside
/// AddApplicationServices (DbContext, Identity, SignalR, data protection, HttpContextAccessor and the assistant
/// notification services) are mirrored as in ClarificationExpirySweepContainerTests; the DbContext never
/// connects.
/// </summary>

using Klacks.Api.Domain.Interfaces.Inbound;
using Klacks.Api.Domain.Models.Authentification;
using Klacks.Api.Infrastructure.Extensions;
using Klacks.Api.Infrastructure.Hubs;
using Klacks.Api.Infrastructure.Inbound;
using Klacks.Api.Infrastructure.Plugins;
using Klacks.Api.Infrastructure.Repositories.Inbound;
using Klacks.Api.Infrastructure.Services;
using Klacks.Plugin.Messaging;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Klacks.UnitTest.Infrastructure.Inbound;

[TestFixture]
public class ClarificationCoordinatorContainerTests
{
    private const string UnusedConnectionString = "Host=unused;Database=unused";

    private static readonly string[] CoordinatorChainTypeNames =
    [
        nameof(ClarificationCoordinator),
        nameof(ClarificationQuestionComposer),
        nameof(EmailReplySender),
        nameof(MessengerReplySender),
        nameof(MessagingPluginClientReplyChannel),
        nameof(InboundIntentAnalysisService),
        nameof(InboundAnalysisNotifier),
        nameof(InboundClarificationRepository),
        nameof(InboundShiftContextReader),
        nameof(CompanyClock)
    ];

    private static IServiceCollection BuildServices()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpContextAccessor();
        services.AddDataProtection();
        services.AddSignalR();
        services.AddIdentity<AppUser, IdentityRole>().AddEntityFrameworkStores<DataBaseContext>().AddDefaultTokenProviders();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddDbContext<DataBaseContext>(options => options.UseNpgsql(UnusedConnectionString));
        services.AddSingleton<IAssistantConnectionTracker, AssistantConnectionTracker>();
        services.AddScoped<IAssistantNotificationService, AssistantNotificationService>();
        services.AddApplicationServices(configuration);
        new MessagingPluginRegistrar().RegisterServices(services, configuration);
        return services;
    }

    private static List<string> ChainFailures(IServiceCollection services)
    {
        var failures = new List<string>();
        try
        {
            using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        }
        catch (AggregateException ex)
        {
            failures.AddRange(ex.InnerExceptions.Select(inner => inner.Message));
        }

        return failures
            .Where(message => CoordinatorChainTypeNames.Any(name => message.Contains(name, StringComparison.Ordinal)))
            .ToList();
    }

    [Test]
    public void TheCoordinatorAndBothReplySendersAreRegistered()
    {
        var services = BuildServices();

        services.ShouldContain(d => d.ServiceType == typeof(IClarificationCoordinator)
                                    && d.ImplementationType == typeof(ClarificationCoordinator)
                                    && d.Lifetime == ServiceLifetime.Scoped);
        services.ShouldContain(d => d.ServiceType == typeof(IInboundReplySender) && d.ImplementationType == typeof(EmailReplySender));
        services.ShouldContain(d => d.ServiceType == typeof(IInboundReplySender) && d.ImplementationType == typeof(MessengerReplySender));
    }

    [Test]
    public void ValidateOnBuild_ReportsNoFailureOnTheCoordinatorChain()
    {
        var chainFailures = ChainFailures(BuildServices());

        chainFailures.ShouldBeEmpty(string.Join(Environment.NewLine, chainFailures));
    }

    [Test]
    public void TheCoordinatorCanBeCreatedFromAScope_WithBothReplySenders()
    {
        using var provider = BuildServices().BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var scope = provider.CreateScope();

        var coordinator = scope.ServiceProvider.GetRequiredService<IClarificationCoordinator>();
        var replySenders = scope.ServiceProvider.GetServices<IInboundReplySender>().ToList();

        coordinator.ShouldBeOfType<ClarificationCoordinator>();
        replySenders.Select(sender => sender.GetType()).ShouldBe(
            [typeof(EmailReplySender), typeof(MessengerReplySender)], ignoreOrder: true);
    }

    [Test]
    public void PositiveControl_ARemovedComposerRegistration_IsReportedOnTheCoordinator()
    {
        var services = BuildServices();
        services.RemoveAll<IClarificationQuestionComposer>();

        var chainFailures = ChainFailures(services);

        chainFailures.ShouldContain(message => message.Contains(nameof(ClarificationCoordinator), StringComparison.Ordinal)
                                               && message.Contains(nameof(IClarificationQuestionComposer), StringComparison.Ordinal));
    }
}
