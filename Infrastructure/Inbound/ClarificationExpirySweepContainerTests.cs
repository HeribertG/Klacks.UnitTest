// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Builds the real application container (AddApplicationServices plus the messaging plugin registrar)
/// and checks the wiring of ClarificationExpirySweep: the hosted service is registered by default and
/// disappears when BackgroundServices:InboundClarificationSweep is off, and ValidateOnBuild reports no
/// failure naming the sweep or the scoped services it resolves per cycle (InboundClarificationRepository,
/// InboundAnalysisNotifier, CompanyClock). ValidateOnBuild only walks constructor dependencies of the
/// registered descriptors, so it proves the sweep's own constructor (IServiceProvider, TimeProvider,
/// IOptions, ILogger) resolves and that each scoped per-cycle service is valid on its own; it does not
/// execute RunCycleAsync. The framework registrations Program.cs adds outside AddApplicationServices
/// (DbContext, Identity, SignalR, data protection, HttpContextAccessor) are mirrored as in
/// MessengerReplyChannelContainerTests, plus the Program.cs-only assistant notification registrations
/// (AssistantConnectionTracker, AssistantNotificationService) that InboundAnalysisNotifier needs; the
/// DbContext never connects.
/// </summary>

using Klacks.Api.Domain.Models.Authentification;
using Klacks.Api.Infrastructure.Extensions;
using Klacks.Api.Infrastructure.Hubs;
using Klacks.Api.Infrastructure.Inbound;
using Klacks.Api.Infrastructure.Repositories.Inbound;
using Klacks.Api.Infrastructure.Services;
using Klacks.Plugin.Messaging;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Klacks.UnitTest.Infrastructure.Inbound;

[TestFixture]
public class ClarificationExpirySweepContainerTests
{
    private const string SweepFlagKey = "BackgroundServices:InboundClarificationSweep";
    private const string UnusedConnectionString = "Host=unused;Database=unused";

    private static readonly string[] SweepChainTypeNames =
    [
        nameof(ClarificationExpirySweep),
        nameof(InboundClarificationRepository),
        nameof(InboundAnalysisNotifier),
        nameof(CompanyClock)
    ];

    private static IServiceCollection BuildServices(string? sweepFlag)
    {
        var settings = new Dictionary<string, string?>();
        if (sweepFlag is not null)
        {
            settings[SweepFlagKey] = sweepFlag;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

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

    private static bool HasSweep(IServiceCollection services) =>
        services.Any(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(ClarificationExpirySweep));

    [Test]
    public void TheSweepIsRegisteredByDefault()
    {
        HasSweep(BuildServices(sweepFlag: null)).ShouldBeTrue();
    }

    [Test]
    public void TheSweepIsNotRegistered_WhenTheFlagIsOff()
    {
        HasSweep(BuildServices(sweepFlag: bool.FalseString)).ShouldBeFalse();
    }

    [Test]
    public void ValidateOnBuild_ReportsNoFailureOnTheSweepChain()
    {
        var failures = new List<string>();
        try
        {
            using var provider = BuildServices(sweepFlag: bool.TrueString)
                .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
        }
        catch (AggregateException ex)
        {
            failures.AddRange(ex.InnerExceptions.Select(inner => inner.Message));
        }

        var chainFailures = failures
            .Where(message => SweepChainTypeNames.Any(name => message.Contains(name, StringComparison.Ordinal)))
            .ToList();

        chainFailures.ShouldBeEmpty(string.Join(Environment.NewLine, chainFailures));
    }
}
