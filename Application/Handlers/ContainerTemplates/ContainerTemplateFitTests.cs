// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Owner rule (2026-11-03) for the container template: an item that reaches outside the template window, by itself or by
/// its travel, briefing or debriefing time, refuses the save (Post and Put) before anything is written. Template 08:00-16:00.
/// The time an item occupies is the planned visit time (TimeRange*) of a TimeRange shift and the own time (StartItem/EndItem)
/// of every other item, as ContainerWorkExpansionService builds the child work; an order window wider than the template is
/// not a violation by itself (weekly tours, 2026-11-15).
/// </summary>

using Klacks.Api.Application.Commands.ContainerTemplates;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Handlers.ContainerTemplates;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.Exceptions;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.ContainerTemplates;

[TestFixture]
public class ContainerTemplateFitTests
{
    private readonly Guid _containerId = Guid.NewGuid();
    private IShiftRepository _shiftRepository = null!;
    private IShiftReader _shiftReader = null!;
    private IContainerTemplateRepository _templateRepository = null!;
    private IContainerLockRepository _lockRepository = null!;
    private List<ContainerTemplate> _added = null!;
    private HashSet<Guid> _timeRangeShiftIds = null!;

    private static TimeOnly T(int h, int m = 0) => new(h, m);

    [SetUp]
    public void SetUp()
    {
        _shiftRepository = Substitute.For<IShiftRepository>();
        _shiftRepository.Get(_containerId).Returns(new Shift
        {
            Id = _containerId, Status = ShiftStatus.OriginalShift, ShiftType = ShiftType.IsContainer,
        });
        _timeRangeShiftIds = [];
        _shiftReader = Substitute.For<IShiftReader>();
        _shiftReader.GetTimeRangeShiftIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call => (IReadOnlySet<Guid>)call.Arg<IReadOnlyCollection<Guid>>().Where(_timeRangeShiftIds.Contains).ToHashSet());
        _templateRepository = Substitute.For<IContainerTemplateRepository>();
        _added = [];
        _templateRepository.Add(Arg.Do<ContainerTemplate>(t => _added.Add(t))).Returns(Task.CompletedTask);
        _templateRepository.GetTemplatesForContainerWithTracking(_containerId).Returns(new List<ContainerTemplate>());
        _lockRepository = Substitute.For<IContainerLockRepository>();
        _lockRepository.IsHeldBy(Arg.Any<string>(), _containerId, Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);
    }

    private ContainerTemplateResource Template(params ContainerTemplateItemResource[] items)
        => Template(T(16), items);

    private ContainerTemplateResource Template(TimeOnly until, params ContainerTemplateItemResource[] items) => new()
    {
        ContainerId = _containerId, FromTime = T(8), UntilTime = until, Weekday = 1,
        ContainerTemplateItems = items.ToList(),
    };

    private static ContainerTemplateItemResource Item(TimeOnly start, TimeOnly end, TimeOnly? travelBefore = null) => new()
    {
        ShiftId = Guid.NewGuid(), Weekday = 1, StartItem = start, EndItem = end,
        TravelTimeBefore = travelBefore ?? TimeOnly.MinValue,
    };

    private ContainerTemplateItemResource Visit(
        TimeOnly windowStart, TimeOnly windowEnd, TimeOnly? plannedStart, TimeOnly? plannedEnd, bool isTimeRangeShift,
        TimeOnly? travelBefore = null, TimeOnly? travelAfter = null)
    {
        var item = new ContainerTemplateItemResource
        {
            ShiftId = Guid.NewGuid(), Weekday = 1, StartItem = windowStart, EndItem = windowEnd,
            TimeRangeStartItem = plannedStart, TimeRangeEndItem = plannedEnd,
            TravelTimeBefore = travelBefore ?? TimeOnly.MinValue, TravelTimeAfter = travelAfter ?? TimeOnly.MinValue,
        };
        if (isTimeRangeShift)
        {
            _timeRangeShiftIds.Add(item.ShiftId!.Value);
        }

        return item;
    }

    private PostContainerTemplatesCommandHandler PostHandler() => new(
        _templateRepository, Substitute.For<IUnitOfWork>(), new ScheduleMapper(), _lockRepository,
        Substitute.For<IUserService>(), Substitute.For<IContainerVisibilityGuard>(), new ContainerFitGuard(_shiftReader),
        Substitute.For<ILogger<PostContainerTemplatesCommandHandler>>());

    private PutContainerTemplatesCommandHandler PutHandler() => new(
        _templateRepository, Substitute.For<IUnitOfWork>(), new ScheduleMapper(), _lockRepository,
        Substitute.For<IUserService>(), Substitute.For<IContainerVisibilityGuard>(), new ContainerFitGuard(_shiftReader),
        Substitute.For<ILogger<PutContainerTemplatesCommandHandler>>());

    private static IEnumerable<TestCaseData> Overhangs()
    {
        yield return new TestCaseData(Item(T(15), T(17))).SetName("ItemEndsAfterUntil");
        yield return new TestCaseData(Item(T(7), T(9))).SetName("ItemStartsBeforeFrom");
        yield return new TestCaseData(Item(T(8, 15), T(10), T(0, 45))).SetName("ItemFitsButTravelBeforeReachesOutside");
    }

    [TestCaseSource(nameof(Overhangs))]
    public async Task PostHandler_RefusesOverhangingItem(ContainerTemplateItemResource item)
    {
        await Should.ThrowAsync<ContainerFitRefusedException>(
            () => PostHandler().Handle(new PostContainerTemplatesCommand(_containerId, [Template(item)]), CancellationToken.None));

        _added.ShouldBeEmpty();
    }

    [TestCaseSource(nameof(Overhangs))]
    public async Task PutHandler_RefusesOverhangingItem(ContainerTemplateItemResource item)
    {
        await Should.ThrowAsync<ContainerFitRefusedException>(
            () => PutHandler().Handle(new PutContainerTemplatesCommand(_containerId, [Template(item)]), CancellationToken.None));

        _added.ShouldBeEmpty();
    }

    [Test]
    public async Task PostHandler_StoresAnItemThatFitsWithItsTravelTime()
    {
        await PostHandler().Handle(
            new PostContainerTemplatesCommand(_containerId, [Template(Item(T(8, 45), T(10), T(0, 45)))]), CancellationToken.None);

        _added.Count.ShouldBe(1);
    }

    [Test]
    public async Task PostHandler_WeeklyTourVisit_PlannedTimeInsideTour_OrderWindowLongerThanTour_IsStored()
    {
        var template = Template(
            T(15, 30),
            Visit(T(8), T(17), T(8, 7), T(8, 52), isTimeRangeShift: true, travelBefore: T(0, 7)),
            Visit(T(8), T(17), T(9), T(9, 45), isTimeRangeShift: true, travelBefore: T(0, 8)));

        await PostHandler().Handle(new PostContainerTemplatesCommand(_containerId, [template]), CancellationToken.None);

        _added.Count.ShouldBe(1);
    }

    [Test]
    public async Task PutHandler_WeeklyTourVisit_PlannedTimeInsideTour_OrderWindowLongerThanTour_IsStored()
    {
        var template = Template(
            T(15, 30), Visit(T(8), T(17), T(8, 7), T(8, 52), isTimeRangeShift: true, travelBefore: T(0, 7)));

        await PutHandler().Handle(new PutContainerTemplatesCommand(_containerId, [template]), CancellationToken.None);

        _added.Count.ShouldBe(1);
    }

    [Test]
    public async Task PostHandler_LastVisitWithTravelBackEndingExactlyAtUntil_IsStored()
    {
        var template = Template(
            T(15, 30), Visit(T(8), T(17), T(14, 30), T(15, 15), isTimeRangeShift: true, travelAfter: T(0, 15)));

        await PostHandler().Handle(new PostContainerTemplatesCommand(_containerId, [template]), CancellationToken.None);

        _added.Count.ShouldBe(1);
    }

    [Test]
    public async Task PostHandler_PlannedTimeEndsAfterTour_IsRefused()
    {
        var template = Template(T(15, 30), Visit(T(8), T(17), T(15), T(15, 45), isTimeRangeShift: true));

        await Should.ThrowAsync<ContainerFitRefusedException>(
            () => PostHandler().Handle(new PostContainerTemplatesCommand(_containerId, [template]), CancellationToken.None));

        _added.ShouldBeEmpty();
    }

    [Test]
    public async Task PostHandler_PlannedTimeInsideButTravelAfterReachesOutside_IsRefused()
    {
        var template = Template(
            T(15, 30), Visit(T(8), T(17), T(14, 30), T(15, 15), isTimeRangeShift: true, travelAfter: T(0, 16)));

        await Should.ThrowAsync<ContainerFitRefusedException>(
            () => PostHandler().Handle(new PostContainerTemplatesCommand(_containerId, [template]), CancellationToken.None));

        _added.ShouldBeEmpty();
    }

    [Test]
    public async Task PostHandler_TimeRangeShiftWithoutPlannedTime_KeepsItsOwnTime_IsRefused()
    {
        var template = Template(T(15, 30), Visit(T(8), T(17), null, null, isTimeRangeShift: true));

        await Should.ThrowAsync<ContainerFitRefusedException>(
            () => PostHandler().Handle(new PostContainerTemplatesCommand(_containerId, [template]), CancellationToken.None));

        _added.ShouldBeEmpty();
    }

    [Test]
    public async Task PostHandler_FixedShiftWithPlannedTimeInside_KeepsItsOwnTime_IsRefused()
    {
        var template = Template(T(15, 30), Visit(T(8), T(17), T(8, 7), T(8, 52), isTimeRangeShift: false));

        await Should.ThrowAsync<ContainerFitRefusedException>(
            () => PostHandler().Handle(new PostContainerTemplatesCommand(_containerId, [template]), CancellationToken.None));

        _added.ShouldBeEmpty();
    }
}
