// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The ERP drop folder startup check is registered unconditionally (also when the background import job is off),
/// so the folder exists on every installation from startup on.
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
}
