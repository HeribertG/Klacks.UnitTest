// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for ThoroughRecalculationOnSurchargeSettingsChangedHandler: the window spans the unlocked
/// real-mode work range without any client scope, no window (no unlocked works) skips the queue, and
/// a scope failure is swallowed defensively.
/// </summary>

using Klacks.Api.Application.DTOs.Scheduling;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Events;
using Klacks.Api.Infrastructure.Events.Handlers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Infrastructure.Events.Handlers;

[TestFixture]
public class ThoroughRecalculationOnSurchargeSettingsChangedHandlerTests
{
    private ISurchargeRecalculationScope _scope = null!;
    private IThoroughRecalculationQueue _queue = null!;
    private ThoroughRecalculationOnSurchargeSettingsChangedHandler _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _scope = Substitute.For<ISurchargeRecalculationScope>();
        _queue = Substitute.For<IThoroughRecalculationQueue>();
        _sut = new ThoroughRecalculationOnSurchargeSettingsChangedHandler(
            _scope, _queue, NullLogger<ThoroughRecalculationOnSurchargeSettingsChangedHandler>.Instance);
    }

    [Test]
    public async Task Handle_UnlockedWorksExist_QueuesTheirFullDateWindowWithoutClientScope()
    {
        var from = new DateOnly(2026, 2, 1);
        var until = new DateOnly(2026, 11, 30);
        _scope.GetUnlockedRealWorkWindowAsync(Arg.Any<CancellationToken>())
            .Returns(new WorkRecalculationWindow(from, until));

        await _sut.HandleAsync(new SurchargeSettingsChangedEvent([SettingKeys.NightRate]));

        _queue.Received(1).QueueRecalculation(from, until, null, null);
    }

    [Test]
    public async Task Handle_NoUnlockedWorks_DoesNotQueue()
    {
        _scope.GetUnlockedRealWorkWindowAsync(Arg.Any<CancellationToken>())
            .Returns((WorkRecalculationWindow?)null);

        await _sut.HandleAsync(new SurchargeSettingsChangedEvent([SettingKeys.NightRate]));

        _queue.DidNotReceiveWithAnyArgs().QueueRecalculation(default, default, null, null);
    }

    [Test]
    public async Task Handle_ScopeFailure_IsSwallowedDefensively()
    {
        _scope.GetUnlockedRealWorkWindowAsync(Arg.Any<CancellationToken>())
            .Returns<Task<WorkRecalculationWindow?>>(_ => throw new InvalidOperationException("boom"));

        await Should.NotThrowAsync(() => _sut.HandleAsync(new SurchargeSettingsChangedEvent([SettingKeys.NightRate])));

        _queue.DidNotReceiveWithAnyArgs().QueueRecalculation(default, default, null, null);
    }
}
