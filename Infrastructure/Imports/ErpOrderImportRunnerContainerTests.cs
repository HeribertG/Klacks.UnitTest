// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Builds the real application container (AddApplicationServices, plus AddErpImportServices and AddMappers as
/// Program.cs calls them, and the messaging plugin registrar, with the framework registrations Program.cs adds outside it mirrored as in ClarificationExpirySweepContainerTests)
/// and checks that ValidateOnBuild reports no failure naming the ledger-based ERP import chain: the runner,
/// the per-file importer, the per-order processor, the inbox registration, the legacy folder migrator, the
/// retention purge and the ledger repository. The
/// DbContext never connects.
/// </summary>

using Klacks.Api.Application.Mappers;
using Klacks.Api.Application.Services.Imports;
using Klacks.Api.Domain.Interfaces.Imports;
using Klacks.Api.Domain.Models.Authentification;
using Klacks.Api.Infrastructure.Extensions;
using Klacks.Api.Infrastructure.Hubs;
using Klacks.Api.Infrastructure.Repositories.Imports;
using Klacks.Api.Infrastructure.Services;
using Klacks.Plugin.Messaging;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Imports;

[TestFixture]
public class ErpOrderImportRunnerContainerTests
{
    private const string UnusedConnectionString = "Host=unused;Database=unused";

    private static readonly string[] ImportChainTypeNames =
    [
        nameof(ErpOrderImportRunner),
        nameof(ErpImportFileImporter),
        nameof(ImportedOrderProcessor),
        nameof(ErpImportInboxRegistrationService),
        nameof(ErpImportLegacyFolderMigrator),
        nameof(ErpImportRetentionService),
        "GetDefaultFilesQueryHandler",
        "RetryErpOrderFileCommandHandler",
        "DeleteErpOrderFileCommandHandler",
        nameof(ErpImportFileRegistrar),
        nameof(ErpImportFileRepository),
        nameof(ErpImportInstanceIdentity)
    ];

    [Test]
    public void ValidateOnBuild_ReportsNoFailureOnTheImportChain()
    {
        // Arrange
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
        services.AddErpImportServices(configuration);
        services.AddMappers();
        new MessagingPluginRegistrar().RegisterServices(services, configuration);

        var failures = new List<string>();

        // Act
        try
        {
            using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
        }
        catch (AggregateException ex)
        {
            failures.AddRange(ex.InnerExceptions.Select(inner => inner.Message));
        }

        // Assert
        services.ShouldContain(d => d.ServiceType == typeof(IErpOrderImportRunner));
        var chainFailures = failures
            .Where(message => ImportChainTypeNames.Any(name => message.Contains(name, StringComparison.Ordinal)))
            .ToList();
        chainFailures.ShouldBeEmpty(string.Join(Environment.NewLine, chainFailures));
    }
}
