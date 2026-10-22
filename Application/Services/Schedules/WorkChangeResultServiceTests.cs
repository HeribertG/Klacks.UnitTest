// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Characterization of WorkChangeResultService.GetClientResultAsync: the period hours come from the period hours
/// service for the given period, the schedule entries are requested for exactly the given three-day window with the
/// caller's visible group list and the analyse token passed through unchanged, and only the rows of the requested
/// client reach the result, in stub order.
/// </summary>

using Klacks.Api.Application.Mappers;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.DTOs.Schedules;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Application.Services.Schedules;

[TestFixture]
public class WorkChangeResultServiceTests
{
    private static readonly DateOnly PeriodStart = new(2027, 5, 1);
    private static readonly DateOnly PeriodEnd = new(2027, 5, 31);
    private static readonly DateOnly ThreeDayStart = new(2027, 5, 11);
    private static readonly DateOnly ThreeDayEnd = new(2027, 5, 13);

    private IPeriodHoursService _periodHoursService = null!;
    private IScheduleEntriesService _scheduleEntriesService = null!;
    private ISelectedGroupContextResolver _groupContextResolver = null!;
    private WorkChangeResultService _service = null!;

    private List<Guid> _visibleGroupIds = null!;
    private Guid _analyseToken;
    private Guid _clientId;
    private Guid _otherClientId;

    [SetUp]
    public void SetUp()
    {
        _periodHoursService = Substitute.For<IPeriodHoursService>();
        _scheduleEntriesService = Substitute.For<IScheduleEntriesService>();
        _groupContextResolver = Substitute.For<ISelectedGroupContextResolver>();

        _visibleGroupIds = [Guid.NewGuid(), Guid.NewGuid()];
        _analyseToken = Guid.NewGuid();
        _clientId = Guid.NewGuid();
        _otherClientId = Guid.NewGuid();

        _groupContextResolver.ResolveVisibleGroupIdsAsync().Returns(_visibleGroupIds);

        _service = new WorkChangeResultService(
            _periodHoursService, _scheduleEntriesService, new ScheduleMapper(), _groupContextResolver);
    }

    [Test]
    public async Task GetClientResultAsync_RequestsTheGivenWindowWithGroupsAndToken_FiltersToTheClient_AndCarriesPeriodHours()
    {
        var periodHours = new PeriodHoursResource { Hours = 12 };
        _periodHoursService.CalculatePeriodHoursAsync(_clientId, PeriodStart, PeriodEnd, _analyseToken)
            .Returns(periodHours);
        var ownRows = GivenScheduleEntries();

        var result = await _service.GetClientResultAsync(
            _clientId, PeriodStart, PeriodEnd, ThreeDayStart, ThreeDayEnd, CancellationToken.None, _analyseToken);

        ThenWindowWasRequested();
        result.ClientId.ShouldBe(_clientId);
        result.PeriodHours.ShouldBeSameAs(periodHours);
        result.ScheduleEntries.Select(e => e.Id).ShouldBe(ownRows);
    }

    private Guid[] GivenScheduleEntries()
    {
        var own = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var foreign = new[] { Guid.NewGuid(), Guid.NewGuid() };
        ScheduleCell[] cells =
        [
            Cell(_clientId, own[0]),
            Cell(_otherClientId, foreign[0]),
            Cell(_clientId, own[1]),
            Cell(_otherClientId, foreign[1]),
        ];
        _scheduleEntriesService
            .GetScheduleEntriesQuery(
                Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<List<Guid>?>(), Arg.Any<Guid?>())
            .Returns(_ => new TestAsyncEnumerable<ScheduleCell>(cells));
        return own;
    }

    private void ThenWindowWasRequested()
    {
        _scheduleEntriesService.Received(1).GetScheduleEntriesQuery(
            ThreeDayStart, ThreeDayEnd, _visibleGroupIds, _analyseToken);
    }

    private static ScheduleCell Cell(Guid clientId, Guid id)
        => new() { Id = id, ClientId = clientId, EntryId = Guid.NewGuid() };
}
