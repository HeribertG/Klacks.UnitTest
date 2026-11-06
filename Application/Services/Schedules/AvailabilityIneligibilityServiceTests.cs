// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for AvailabilityIneligibilityService: the rows of the day after the last slot are loaded, so a night
/// shift on the last day is checked against the next morning's availability (owner rule 2026-11-06).
/// </summary>

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.Models.Staffs;

namespace Klacks.UnitTest.Application.Services.Schedules;

[TestFixture]
public class AvailabilityIneligibilityServiceTests
{
    private static readonly DateOnly LastDay = new(2026, 12, 6);
    private static readonly Guid Agent = Guid.NewGuid();
    private static readonly Guid NightShift = Guid.NewGuid();

    private static AvailabilityShiftSlot NightSlot() => new(NightShift, LastDay, new TimeOnly(23, 0), new TimeOnly(7, 0));

    private static List<ClientAvailability> MorningBlockedOnNextDay()
        => Enumerable.Range(0, 7)
            .Select(hour => new ClientAvailability { ClientId = Agent, Date = LastDay.AddDays(1), Hour = hour, IsAvailable = false })
            .ToList();

    [Test]
    public async Task GetAsync_LoadsTheDayAfterTheLastSlot()
    {
        var repository = Substitute.For<IClientAvailabilityRepository>();
        repository.GetByDateRange(Arg.Any<DateOnly>(), Arg.Any<DateOnly>()).Returns(new List<ClientAvailability>());

        await new AvailabilityIneligibilityService(repository).GetAsync([Agent], [NightSlot()], CancellationToken.None);

        await repository.Received(1).GetByDateRange(LastDay, LastDay.AddDays(1));
    }

    [Test]
    public async Task GetAsync_NightShiftOntoABlockedMorning_IsIneligible()
    {
        var repository = Substitute.For<IClientAvailabilityRepository>();
        repository.GetByDateRange(LastDay, LastDay.AddDays(1)).Returns(MorningBlockedOnNextDay());

        var result = await new AvailabilityIneligibilityService(repository).GetAsync([Agent], [NightSlot()], CancellationToken.None);

        result.ShouldContain((Agent.ToString(), NightShift, LastDay));
    }

    [Test]
    public async Task GetAsync_NightShiftOntoAFreeMorning_IsEligible()
    {
        var repository = Substitute.For<IClientAvailabilityRepository>();
        repository.GetByDateRange(Arg.Any<DateOnly>(), Arg.Any<DateOnly>()).Returns(new List<ClientAvailability>());

        var result = await new AvailabilityIneligibilityService(repository).GetAsync([Agent], [NightSlot()], CancellationToken.None);

        result.ShouldBeEmpty();
    }
}
