// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.DTOs.Schedules.AutoWizard;
using Klacks.Api.Application.DTOs.Schedules.HolisticHarmonizer;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Infrastructure.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Schedules;

/// <summary>
/// The four JobTerminalStateCache registrations are parameterless AddSingleton calls, so the container
/// has to pick the constructor by reflection. Program.cs enables neither ValidateOnBuild nor
/// ValidateScopes, so a constructor dependency that nobody registered does not fail at startup - it
/// fails on first resolve, i.e. during the first real wizard, harmonizer, holistic or auto-wizard run,
/// with a green build the whole way. These tests force that resolve.
/// </summary>
[TestFixture]
public class JobTerminalStateCacheRegistrationTests
{
    private ServiceProvider _provider = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddApplicationServices(new ConfigurationBuilder().Build());
        _provider = services.BuildServiceProvider();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown() => _provider.Dispose();

    [Test]
    public void WizardVariant_Resolves() =>
        _provider.GetRequiredService<JobTerminalStateCache<WizardJobResultDto>>().ShouldNotBeNull();

    [Test]
    public void HarmonizerVariant_Resolves() =>
        _provider.GetRequiredService<JobTerminalStateCache<HarmonizerJobResultDto>>().ShouldNotBeNull();

    [Test]
    public void HolisticHarmonizerVariant_Resolves() =>
        _provider.GetRequiredService<JobTerminalStateCache<HolisticHarmonizerRunResponse>>().ShouldNotBeNull();

    [Test]
    public void AutoWizardVariant_Resolves() =>
        _provider.GetRequiredService<JobTerminalStateCache<AutoWizardJobResultDto>>().ShouldNotBeNull();
}
