// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Regression tests for the travel-time pairing extracted from ScheduleTimelineBackgroundService (it was private
/// there, so these run against the split code only): the lookup budget, skipped cross-day pairs, missing and
/// equal locations, and a routing answer without a travel time.
/// </summary>

using Klacks.Api.Application.DTOs.Notifications;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.RouteOptimization;
using Klacks.Api.Infrastructure.Services.ScheduleTimeline;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Services.ScheduleTimeline;

[TestFixture]
public class TimelineTravelTimeEntriesTests
{
    private const string ClientName = "Muster Anna";

    private static readonly DateTime Tuesday = new(2026, 3, 3, 0, 0, 0, DateTimeKind.Utc);

    private ITravelTimeCalculationService _travel = null!;
    private Guid _clientId;
    private List<ScheduleValidationNotificationDto> _entries = null!;

    [SetUp]
    public void SetUp()
    {
        _travel = Substitute.For<ITravelTimeCalculationService>();
        _travel.CalculateTravelTimeAsync(Arg.Any<Address>(), Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns(TimeSpan.FromMinutes(30));
        _clientId = Guid.NewGuid();
        _entries = [];
    }

    [Test]
    public async Task AddAsync_StopsAtTheLookupBudgetAndReportsTheLookupsSpent()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var timeline = Timeline(Block(a, 6, 8), Block(b, 8.25, 10), Block(a, 10.25, 12));

        var spent = await Add(timeline, Lookup((a, Address("A-Strasse 1")), (b, Address("B-Weg 2"))), lookupBudget: 1);

        spent.ShouldBe(1);
        _entries.ShouldHaveSingleItem().Comment.ShouldBe(ScheduleValidationKeys.TravelTimeError);
        await _travel.ReceivedWithAnyArgs(1).CalculateTravelTimeAsync(default!, default!, default);
    }

    [Test]
    public async Task AddAsync_PairStartingOnDifferentDays_IsNotCompared()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var timeline = Timeline(Block(a, 20, 23), Block(b, 24.25, 26));

        var spent = await Add(timeline, Lookup((a, Address("A-Strasse 1")), (b, Address("B-Weg 2"))));

        spent.ShouldBe(0);
        _entries.ShouldBeEmpty();
    }

    [Test]
    public async Task AddAsync_ShiftWithoutLocation_IsSkipped()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var timeline = Timeline(Block(a, 6, 8), Block(b, 8.25, 10));

        var spent = await Add(timeline, Lookup((a, Address("A-Strasse 1")), (b, null)));

        spent.ShouldBe(0);
        _entries.ShouldBeEmpty();
    }

    [Test]
    public async Task AddAsync_SameStreetZipAndCityIgnoringCase_CountsAsTheSameLocation()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var timeline = Timeline(Block(a, 6, 8), Block(b, 8.25, 10));
        var upper = Address("BAHNHOFSTRASSE 1");
        var lower = Address("bahnhofstrasse 1");

        var spent = await Add(timeline, Lookup((a, upper), (b, lower)));

        spent.ShouldBe(0);
        _entries.ShouldBeEmpty();
    }

    [Test]
    public async Task AddAsync_RoutingWithoutAnAnswer_SpendsTheLookupButAddsNothing()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var timeline = Timeline(Block(a, 6, 8), Block(b, 8.25, 10));
        _travel.CalculateTravelTimeAsync(Arg.Any<Address>(), Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns((TimeSpan?)null);

        var spent = await Add(timeline, Lookup((a, Address("A-Strasse 1")), (b, Address("B-Weg 2"))));

        spent.ShouldBe(1);
        _entries.ShouldBeEmpty();
    }

    [Test]
    public async Task AddAsync_FewerThanTwoWorkBlocks_SpendsNothing()
    {
        var a = Guid.NewGuid();
        var timeline = Timeline(Block(a, 6, 8));

        var spent = await Add(timeline, Lookup((a, Address("A-Strasse 1"))));

        spent.ShouldBe(0);
        _entries.ShouldBeEmpty();
    }

    private Task<int> Add(ClientTimeline timeline, Dictionary<Guid, Address?> lookup, int lookupBudget = int.MaxValue)
        => TimelineTravelTimeEntries.AddAsync(_entries, timeline, ClientName, lookup, _travel, true, CancellationToken.None, lookupBudget);

    private ClientTimeline Timeline(params ScheduleBlock[] blocks)
    {
        var timeline = new ClientTimeline(_clientId);
        timeline.AddBlocks(blocks);
        timeline.SortBlocks();
        return timeline;
    }

    private ScheduleBlock Block(Guid shiftId, double startHour, double endHour)
        => new(Guid.NewGuid(), ScheduleBlockType.Work, _clientId, Tuesday.AddHours(startHour), Tuesday.AddHours(endHour), shiftId);

    private static Dictionary<Guid, Address?> Lookup(params (Guid ShiftId, Address? Address)[] entries)
        => entries.ToDictionary(e => e.ShiftId, e => e.Address);

    private static Address Address(string street)
        => new() { Id = Guid.NewGuid(), Street = street, Zip = "8001", City = "Zürich" };
}
