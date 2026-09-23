// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Builds the real application container (AddApplicationServices plus the messaging plugin registrar,
/// with the messenger intent observer switched on) with ValidateOnBuild and checks that no descriptor on
/// the messenger clarification chain fails validation: MessengerReplySender, the
/// MessagingPluginClientReplyChannel adapter, MessagingService and MessengerIntentObserver. MessagingService
/// sits inside the ILLMService graph and receives the messenger observers through its constructor, so a
/// cycle through the new reply channel would stop the host from booting (messaging-observer-di-leaf).
/// The framework registrations Program.cs adds outside AddApplicationServices and that the chain needs
/// (DbContext, Identity, SignalR, data protection, HttpContextAccessor) are mirrored here; the DbContext
/// never connects. Unrelated descriptors that need more host configuration are ignored; a failure that
/// names one of the chain types is not, so a missing registration on the chain cannot hide a cycle
/// behind it. A positive control registers an observer that takes the reply senders and proves the
/// check sees the resulting cycle. Not covered: registrations made only in Program.cs, and provider
/// adapters resolved at runtime through MessagingProviderAdapterFactory (a switch over the provider
/// type string calling IServiceProvider.GetRequiredService per concrete provider class, e.g.
/// TelegramMessagingProvider) - ValidateOnBuild only walks constructor dependencies of registered
/// service DESCRIPTORS, it never calls Create(), so a missing registration or a dependency cycle inside
/// one specific provider adapter would only surface the first time that adapter is actually created.
/// </summary>

using Klacks.Api.Domain.Interfaces.Inbound;
using Klacks.Api.Domain.Models.Authentification;
using Klacks.Api.Infrastructure.Extensions;
using Klacks.Api.Infrastructure.Inbound;
using Klacks.Api.Infrastructure.Plugins;
using Klacks.Plugin.Contracts;
using Klacks.Plugin.Messaging;
using Klacks.Plugin.Messaging.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Klacks.UnitTest.Infrastructure.Plugins;

[TestFixture]
public class MessengerReplyChannelContainerTests
{
    private const string MessengerIntentAnalysisKey = "BackgroundServices:MessengerIntentAnalysis";
    private const string UnusedConnectionString = "Host=unused;Database=unused";
    private const string CircularDependencyMarker = "circular dependency";

    private static readonly string[] ChainTypeNames =
    [
        nameof(MessengerReplySender),
        nameof(MessagingPluginClientReplyChannel),
        nameof(MessagingService),
        nameof(MessengerIntentObserver)
    ];

    private static IServiceCollection BuildServices()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [MessengerIntentAnalysisKey] = bool.TrueString })
            .Build();

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
        return services;
    }

    [Test]
    public void TheMessengerReplyChainIsRegistered()
    {
        var services = BuildServices();

        services.ShouldContain(d => d.ServiceType == typeof(IInboundReplySender) && d.ImplementationType == typeof(MessengerReplySender));
        services.ShouldContain(d => d.ServiceType == typeof(IClientMessengerReplyChannel) && d.ImplementationType == typeof(MessagingPluginClientReplyChannel));
        services.ShouldContain(d => d.ImplementationType == typeof(MessengerIntentObserver));
    }

    [Test]
    public void ValidateOnBuild_ReportsNoFailureOnTheMessengerReplyChain()
    {
        var chainFailures = ChainFailures(BuildServices());

        chainFailures.ShouldBeEmpty(string.Join(Environment.NewLine, chainFailures));
    }

    [Test]
    public void PositiveControl_AnObserverTakingTheReplySenders_IsReportedAsACycle()
    {
        var services = BuildServices();
        services.AddScoped<IInboundClientMessengerObserver, ReplySenderConsumingObserver>();

        var chainFailures = ChainFailures(services);

        chainFailures.ShouldContain(message => message.Contains(CircularDependencyMarker, StringComparison.OrdinalIgnoreCase));
    }

    private static List<string> ChainFailures(IServiceCollection services)
    {
        var failures = new List<string>();
        try
        {
            using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
        }
        catch (AggregateException ex)
        {
            failures.AddRange(ex.InnerExceptions.Select(inner => inner.Message));
        }

        return failures
            .Where(message => ChainTypeNames.Any(name => message.Contains(name, StringComparison.Ordinal)))
            .ToList();
    }

    private sealed class ReplySenderConsumingObserver : IInboundClientMessengerObserver
    {
        public ReplySenderConsumingObserver(IEnumerable<IInboundReplySender> replySenders)
        {
            _ = replySenders;
        }

        public Task OnInboundMessageAsync(InboundClientMessengerMessage message, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
