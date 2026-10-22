// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.Handlers.Shifts;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Application.Queries.Shifts;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Services.ShiftSchedule;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Handlers.Shifts;

/// <summary>
/// Shifts/ByIds over the real ShiftRepository (EF InMemory): only the requested real-plan shifts (no scenario copy,
/// no scenario source link), the caller's group restriction (null = all, empty = only group-less shifts, a set =
/// group-less plus shifts in one of those groups), the repository order (original id, from date, start time) and the
/// client with its addresses on every row. SQL translation and PostgreSQL's uuid order are pinned by the
/// integration test ShiftReadShapesTests.
/// </summary>
[TestFixture]
public class GetShiftsByIdsQueryHandlerTests
{
    private static readonly Guid OrderId = Guid.NewGuid();
    private static readonly Guid GroupA = Guid.NewGuid();
    private static readonly Guid GroupB = Guid.NewGuid();

    private InMemoryShiftStore _store = null!;
    private IShiftGroupFilterService _groupFilter = null!;
    private GetShiftsByIdsQueryHandler _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _store = new InMemoryShiftStore();
        _groupFilter = Substitute.For<IShiftGroupFilterService>();
        _groupFilter.ResolveShiftListGroupIdsAsync(null).Returns((List<Guid>?)null);
        _sut = new GetShiftsByIdsQueryHandler(
            _store.Shifts, _groupFilter, new ScheduleMapper(), NullLogger<GetShiftsByIdsQueryHandler>.Instance);
    }

    [TearDown]
    public void TearDown() => _store.Dispose();

    [Test]
    public async Task Unrestricted_ReturnsTheRequestedRealPlanShifts_InFromDateThenStartTimeOrder()
    {
        var late = MakeShift("late", new DateOnly(2026, 3, 5), new TimeOnly(8, 0));
        var earlyNoon = MakeShift("earlyNoon", new DateOnly(2026, 3, 1), new TimeOnly(12, 0));
        var earlyMorning = MakeShift("earlyMorning", new DateOnly(2026, 3, 1), new TimeOnly(6, 0));
        var notRequested = MakeShift("notRequested", new DateOnly(2026, 3, 1), new TimeOnly(7, 0));
        _store.Seed(late, earlyNoon, earlyMorning, notRequested);

        var result = await HandleAsync(late.Id, earlyNoon.Id, earlyMorning.Id, late.Id);

        result.Select(r => r.Name).ShouldBe(["earlyMorning", "earlyNoon", "late"]);
    }

    [Test]
    public async Task ScenarioCopies_AreDropped_LikeUnknownIds()
    {
        var real = MakeShift("real", new DateOnly(2026, 3, 1), new TimeOnly(6, 0));
        var tokenCopy = MakeShift("tokenCopy", new DateOnly(2026, 3, 1), new TimeOnly(7, 0));
        tokenCopy.AnalyseToken = Guid.NewGuid();
        var sourceLinked = MakeShift("sourceLinked", new DateOnly(2026, 3, 1), new TimeOnly(8, 0));
        sourceLinked.ScenarioSourceShiftId = real.Id;
        _store.Seed(real, tokenCopy, sourceLinked);

        var result = await HandleAsync(real.Id, tokenCopy.Id, sourceLinked.Id, Guid.NewGuid());

        result.Select(r => r.Name).ShouldBe(["real"]);
    }

    [Test]
    public async Task EmptyRestriction_KeepsOnlyGroupLessShifts_AndASetAddsShiftsInOneOfItsGroups()
    {
        var groupLess = MakeShift("groupLess", new DateOnly(2026, 3, 1), new TimeOnly(6, 0));
        var inA = MakeShift("inA", new DateOnly(2026, 3, 1), new TimeOnly(7, 0), GroupA);
        var inB = MakeShift("inB", new DateOnly(2026, 3, 1), new TimeOnly(8, 0), GroupB);
        var deletedLinkOnly = MakeShift("deletedLinkOnly", new DateOnly(2026, 3, 1), new TimeOnly(9, 0), GroupB);
        deletedLinkOnly.GroupItems.Single().IsDeleted = true;
        _store.Seed(groupLess, inA, inB, deletedLinkOnly);
        var requested = new[] { groupLess.Id, inA.Id, inB.Id, deletedLinkOnly.Id };

        _groupFilter.ResolveShiftListGroupIdsAsync(null).Returns(new List<Guid>());
        var none = await HandleAsync(requested);

        _groupFilter.ResolveShiftListGroupIdsAsync(null).Returns(new List<Guid> { GroupA });
        var onlyA = await HandleAsync(requested);

        none.Select(r => r.Name).ShouldBe(["groupLess", "deletedLinkOnly"]);
        onlyA.Select(r => r.Name).ShouldBe(["groupLess", "inA", "deletedLinkOnly"]);
    }

    [Test]
    public async Task EveryRow_CarriesItsClientWithTheClientsAddresses()
    {
        var client = new Client
        {
            Id = Guid.NewGuid(),
            Name = "Customer",
            Addresses =
            [
                new Address { Id = Guid.NewGuid(), Street = "Hauptstrasse 1", Zip = "2502", City = "Biel", Country = "CH", ValidFrom = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
                new Address { Id = Guid.NewGuid(), Street = "Bahnhofplatz 2", Zip = "2502", City = "Biel", Country = "CH", ValidFrom = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
            ],
        };
        var shift = MakeShift("withClient", new DateOnly(2026, 3, 1), new TimeOnly(6, 0));
        shift.ClientId = client.Id;
        shift.Client = client;
        _store.Seed(shift);

        var result = await HandleAsync(shift.Id);

        result.Single().Client.ShouldNotBeNull();
        result.Single().Client!.Addresses.Count.ShouldBe(2);
    }

    [Test]
    public async Task NoIds_ReturnsEmpty_WithoutResolvingTheRestriction()
    {
        var result = await _sut.Handle(new GetShiftsByIdsQuery([]), CancellationToken.None);

        result.ShouldBeEmpty();
        await _groupFilter.DidNotReceiveWithAnyArgs().ResolveShiftListGroupIdsAsync(default);
    }

    private Task<List<Klacks.Api.Application.DTOs.Schedules.ShiftResource>> HandleAsync(params Guid[] ids) =>
        _sut.Handle(new GetShiftsByIdsQuery(ids.ToList()), CancellationToken.None);

    private static Shift MakeShift(string name, DateOnly fromDate, TimeOnly start, params Guid[] groupIds)
    {
        var id = Guid.NewGuid();
        return new Shift
        {
            Id = id,
            Name = name,
            Abbreviation = name,
            Status = ShiftStatus.SplitShift,
            ShiftType = ShiftType.IsTask,
            OriginalId = OrderId,
            FromDate = fromDate,
            StartShift = start,
            EndShift = start.AddHours(4),
            GroupItems = groupIds
                .Select(groupId => new GroupItem { Id = Guid.NewGuid(), GroupId = groupId, ShiftId = id })
                .ToList(),
        };
    }
}
