// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The ERP drop folder startup check is registered independently of the background import job,
/// so the folder exists on every installation from startup on; its own switch BackgroundServices:ErpDropFolderStartup turns it off (test hosts).
/// </summary>

using Klacks.Api.Application.Services.Imports;
using Klacks.Api.Infrastructure.Extensions;
using Klacks.Api.Infrastructure.Services.Imports;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Klacks.UnitTest.Infrastructure.Imports;

[TestFixture]
public class ErpDropFolderStartupRegistrationTests
{
    [Test]
    public void AddErpImportServices_RegistersTheStartupServiceAndItsInitializer_EvenWithTheImportJobOff()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["BackgroundServices:ErpOrderImport"] = "false" })
            .Build();
        var services = new ServiceCollection();

        services.AddErpImportServices(configuration);

        services.ShouldContain(descriptor =>
            descriptor.ServiceType == typeof(IHostedService)
            && descriptor.ImplementationType == typeof(ErpDropFolderStartupService));
        services.ShouldContain(descriptor =>
            descriptor.ServiceType == typeof(ErpDropFolderInitializer)
            && descriptor.Lifetime == ServiceLifetime.Scoped);
    }

    [Test]
    public void AddErpImportServices_SkipsTheStartupService_WhenItsSwitchIsOff()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["BackgroundServices:ErpDropFolderStartup"] = "false" })
            .Build();
        var services = new ServiceCollection();

        services.AddErpImportServices(configuration);

        services.ShouldNotContain(descriptor =>
            descriptor.ServiceType == typeof(IHostedService)
            && descriptor.ImplementationType == typeof(ErpDropFolderStartupService));
    }
}
