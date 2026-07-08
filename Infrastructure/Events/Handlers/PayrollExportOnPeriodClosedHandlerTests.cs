// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the reference PayrollExportOnPeriodClosedHandler: feature-flag gating of both branches.
/// </summary>

using Shouldly;
using Klacks.Api.Application.Interfaces.Plugins;
using Klacks.Api.Domain.Events;
using Klacks.Api.Infrastructure.Events.Handlers;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Klacks.UnitTest.Infrastructure.Events.Handlers;

[TestFixture]
public class PayrollExportOnPeriodClosedHandlerTests
{
    private IFeaturePluginService _featurePluginService = null!;
    private ILogger<PayrollExportOnPeriodClosedHandler> _logger = null!;
    private PayrollExportOnPeriodClosedHandler _handler = null!;

    private static PeriodClosedEvent SampleEvent() => new(
        new DateOnly(2026, 1, 1),
        new DateOnly(2026, 1, 31),
        null,
        10,
        3,
        31,
        "admin-user");

    [SetUp]
    public void Setup()
    {
        _featurePluginService = Substitute.For<IFeaturePluginService>();
        _logger = Substitute.For<ILogger<PayrollExportOnPeriodClosedHandler>>();
        _handler = new PayrollExportOnPeriodClosedHandler(_featurePluginService, _logger);
    }

    [Test]
    public async Task HandleAsync_SkipsAndChecksGate_WhenFeatureDisabled()
    {
        _featurePluginService.IsEnabled(PayrollExportOnPeriodClosedHandler.FeaturePluginName).Returns(false);

        await Should.NotThrowAsync(async () => await _handler.HandleAsync(SampleEvent(), CancellationToken.None));

        _featurePluginService.Received(1).IsEnabled(PayrollExportOnPeriodClosedHandler.FeaturePluginName);
    }

    [Test]
    public async Task HandleAsync_RunsAndChecksGate_WhenFeatureEnabled()
    {
        _featurePluginService.IsEnabled(PayrollExportOnPeriodClosedHandler.FeaturePluginName).Returns(true);

        await Should.NotThrowAsync(async () => await _handler.HandleAsync(SampleEvent(), CancellationToken.None));

        _featurePluginService.Received(1).IsEnabled(PayrollExportOnPeriodClosedHandler.FeaturePluginName);
    }
}
