// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Builds the real application container (AddApplicationServices plus the messaging plugin registrar) and
/// checks the wiring of the two classes that now resolve their language through IInstallationLanguageResolver:
/// EscalationNotifier (with its new EscalationHandoffTextService) and ProactiveMessengerTextComposer. The
/// escalation chain is reached lazily and its failures are logged, not thrown, so a broken registration
/// would show up only as a silently missing handoff note. These tests close that gap: all three are
/// registered scoped (never Singleton, because the resolver reads the settings through the scoped
/// repository), ValidateOnBuild reports no failure naming them or a type on their constructor chain, they
/// can be created from a scope, and a positive control proves the check sees a removed dependency. The
/// framework registrations Program.cs adds outside AddApplicationServices are mirrored as in
/// ClarificationCoordinatorContainerTests; the DbContext never connects.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Triggers;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Authentification;
using Klacks.Api.Infrastructure.Extensions;
using Klacks.Api.Infrastructure.Hubs;
using Klacks.Api.Infrastructure.Services;
using Klacks.Api.Infrastructure.Services.Assistant.Escalation;
using Klacks.Api.Infrastructure.Services.Settings;
using Klacks.Plugin.Messaging;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Klacks.UnitTest.Infrastructure.Services.Assistant.Escalation;

[TestFixture]
public class EscalationTextsContainerTests
{
    private const string UnusedConnectionString = "Host=unused;Database=unused";

    private static readonly string[] ChainTypeNames =
    [
        nameof(EscalationNotifier),
        nameof(EscalationHandoffTextService),
        nameof(ProactiveMessengerTextComposer),
        nameof(InstallationLanguageResolver)
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
            .Where(message => ChainTypeNames.Any(name => message.Contains(name, StringComparison.Ordinal)))
            .ToList();
    }

    [Test]
    public void TheNotifierTheHandoffTextServiceAndTheComposerAreRegisteredScoped()
    {
        var services = BuildServices();

        services.ShouldContain(d => d.ServiceType == typeof(IEscalationNotifier)
                                    && d.ImplementationType == typeof(EscalationNotifier)
                                    && d.Lifetime == ServiceLifetime.Scoped);
        services.ShouldContain(d => d.ServiceType == typeof(IEscalationHandoffTextService)
                                    && d.ImplementationType == typeof(EscalationHandoffTextService)
                                    && d.Lifetime == ServiceLifetime.Scoped);
        services.ShouldContain(d => d.ServiceType == typeof(IProactiveMessengerTextComposer)
                                    && d.ImplementationType == typeof(ProactiveMessengerTextComposer)
                                    && d.Lifetime == ServiceLifetime.Scoped);
        services.ShouldContain(d => d.ServiceType == typeof(IInstallationLanguageResolver)
                                    && d.Lifetime == ServiceLifetime.Scoped);
    }

    [Test]
    public void ValidateOnBuild_ReportsNoFailureOnTheEscalationTextChain()
    {
        var chainFailures = ChainFailures(BuildServices());

        chainFailures.ShouldBeEmpty(string.Join(Environment.NewLine, chainFailures));
    }

    [Test]
    public void TheNotifierTheHandoffTextServiceAndTheComposerCanBeCreatedFromAScope()
    {
        using var provider = BuildServices().BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<IEscalationHandoffTextService>().ShouldBeOfType<EscalationHandoffTextService>();
        scope.ServiceProvider.GetRequiredService<IProactiveMessengerTextComposer>().ShouldBeOfType<ProactiveMessengerTextComposer>();
        scope.ServiceProvider.GetRequiredService<IEscalationNotifier>().ShouldBeOfType<EscalationNotifier>();
    }

    [Test]
    public void PositiveControl_ARemovedHandoffTextService_IsReportedOnTheNotifier()
    {
        var services = BuildServices();
        services.RemoveAll<IEscalationHandoffTextService>();

        var chainFailures = ChainFailures(services);

        chainFailures.ShouldContain(message => message.Contains(nameof(EscalationNotifier), StringComparison.Ordinal)
                                               && message.Contains(nameof(IEscalationHandoffTextService), StringComparison.Ordinal));
    }

    [Test]
    public void PositiveControl_ARemovedLanguageResolver_IsReportedOnTheComposer()
    {
        var services = BuildServices();
        services.RemoveAll<IInstallationLanguageResolver>();

        var chainFailures = ChainFailures(services);

        chainFailures.ShouldContain(message => message.Contains(nameof(ProactiveMessengerTextComposer), StringComparison.Ordinal)
                                               && message.Contains(nameof(IInstallationLanguageResolver), StringComparison.Ordinal));
    }
}
