// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests that the setup diagnosis service and skill resolve from the plugin's DI registration once the
/// host-provided services (DbContext, employee reader, settings reader) are present, and that DI picks
/// the constructor without the test clock.
/// </summary>
using Klacks.Plugin.Contracts;
using Klacks.Plugin.Messaging;
using Klacks.Plugin.Messaging.Application.Interfaces;
using Klacks.Plugin.Messaging.Application.Services.Setup;
using Klacks.Plugin.Messaging.Skills;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Plugins.Messaging.Setup;

[TestFixture]
public class MessagingSetupRegistrationTests
{
    private ServiceProvider _serviceProvider = null!;

    [SetUp]
    public void SetUp()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped(_ => new DbContext(new DbContextOptionsBuilder().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options));
        services.AddScoped(_ => Substitute.For<IEmployeeClientReader>());
        services.AddScoped(_ => Substitute.For<IPluginSettingsReader>());

        new MessagingPluginRegistrar().RegisterServices(services, new ConfigurationBuilder().Build());

        _serviceProvider = services.BuildServiceProvider();
    }

    [TearDown]
    public void TearDown()
    {
        _serviceProvider.Dispose();
    }

    [Test]
    public void RegisterServices_ResolvesSetupDiagnosticsService()
    {
        using var scope = _serviceProvider.CreateScope();

        var service = scope.ServiceProvider.GetRequiredService<IMessagingSetupDiagnosticsService>();

        service.ShouldBeOfType<MessagingSetupDiagnosticsService>();
    }

    [Test]
    public void RegisterServices_ResolvesDiagnoseMessagingSetupSkill()
    {
        using var scope = _serviceProvider.CreateScope();

        scope.ServiceProvider.GetRequiredService<DiagnoseMessagingSetupSkill>().ShouldNotBeNull();
    }
}
