// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Templates and overrides carry no analyse token and are not cloned. A container work of a scenario on a cloned
/// container without an own template or override for the day expands from those of the real container
/// (ScenarioSourceShiftId), places each task on its clone in that scenario (the real shift when there is no clone) and
/// gives its sub-works and work changes the token of the container work. A real-plan container work never reads the
/// clones.
/// </summary>

using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Services.Schedules;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Services.Schedules;

[TestFixture]
public class ContainerWorkExpansionScenarioSourceTests
{
    private static readonly DateOnly Day = new(2026, 11, 2);
    private static readonly TimeOnly Quarter = new(0, 15);

    private readonly Guid _token = Guid.NewGuid();
    private readonly Guid _realContainerId = Guid.NewGuid();
    private readonly Guid _cloneContainerId = Guid.NewGuid();
    private readonly Guid _realTaskId = Guid.NewGuid();
    private readonly Guid _cloneTaskId = Guid.NewGuid();
    private readonly Guid _realTaskWithoutCloneId = Guid.NewGuid();

    private IContainerTemplateRepository _templates = null!;
    private IContainerShiftOverrideRepository _overrides = null!;
    private IShiftReader _shiftReader = null!;
    private ContainerWorkExpansionService _service = null!;
    private readonly List<Work> _addedWorks = [];
    private readonly List<WorkChange> _addedChanges = [];

    [SetUp]
    public void Setup()
    {
        _addedWorks.Clear();
        _addedChanges.Clear();
        _templates = Substitute.For<IContainerTemplateRepository>();
        _templates.GetTemplatesForContainer(Arg.Any<Guid>()).Returns(new List<ContainerTemplate>());
        _overrides = Substitute.For<IContainerShiftOverrideRepository>();
        _shiftReader = Substitute.For<IShiftReader>();
        _shiftReader.GetScenarioClonesBySourceAsync(Arg.Any<IReadOnlyCollection<Guid>>(), _token, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, Guid> { [_realTaskId] = _cloneTaskId });

        var works = Substitute.For<IWorkRepository>();
        works.When(r => r.Add(Arg.Any<Work>())).Do(call => _addedWorks.Add(call.Arg<Work>()));
        var workChanges = Substitute.For<IWorkChangeRepository>();
        workChanges.When(r => r.Add(Arg.Any<WorkChange>())).Do(call => _addedChanges.Add(call.Arg<WorkChange>()));

        var shifts = Substitute.For<IShiftRepository>();
        shifts.Get(_cloneContainerId).Returns(new Shift
        {
            Id = _cloneContainerId, ShiftType = ShiftType.IsContainer, AnalyseToken = _token, ScenarioSourceShiftId = _realContainerId
        });
        shifts.Get(_realContainerId).Returns(new Shift { Id = _realContainerId, ShiftType = ShiftType.IsContainer });

        _service = new ContainerWorkExpansionService(
            _templates,
            _overrides,
            works,
            Substitute.For<IBreakRepository>(),
            workChanges,
            shifts,
            Substitute.For<IWorkWriteGuard>(),
            Substitute.For<IContainerWorkChildrenManager>(),
            _shiftReader,
            Substitute.For<ILogger<ContainerWorkExpansionService>>());
    }

    [Test]
    public async Task ScenarioWork_CloneWithoutOwnTemplate_ExpandsTheRealTemplateOntoTheClonesWithTheToken()
    {
        GivenTemplate(_realContainerId, _realTaskId, _realTaskWithoutCloneId);

        await _service.ExpandAsync(ContainerWork(_cloneContainerId, _token), Day);

        _addedWorks.Select(work => work.ShiftId).ShouldBe([_cloneTaskId, _realTaskWithoutCloneId]);
        _addedWorks.ShouldAllBe(work => work.AnalyseToken == _token);
        _addedChanges.Count.ShouldBe(8);
        _addedChanges.ShouldAllBe(change => change.AnalyseToken == _token);
    }

    [Test]
    public async Task ScenarioWork_CloneWithOwnTemplate_KeepsItsOwnTemplateAndItsItemShifts()
    {
        GivenTemplate(_realContainerId, _realTaskId);
        GivenTemplate(_cloneContainerId, _realTaskWithoutCloneId);

        await _service.ExpandAsync(ContainerWork(_cloneContainerId, _token), Day);

        _addedWorks.Select(work => work.ShiftId).ShouldBe([_realTaskWithoutCloneId]);
        await _shiftReader.DidNotReceiveWithAnyArgs().GetScenarioClonesBySourceAsync(default!, default, default);
    }

    [Test]
    public async Task ScenarioWork_CloneWithoutOwnOverride_ExpandsTheOverrideOfTheRealContainerOntoTheClones()
    {
        _overrides.GetByContainerAndDateWithItems(_realContainerId, Day, Arg.Any<CancellationToken>())
            .Returns(new ContainerShiftOverride
            {
                ContainerShiftOverrideItems = [new ContainerShiftOverrideItem { ShiftId = _realTaskId, StartItem = new TimeOnly(8, 0), EndItem = new TimeOnly(9, 0) }]
            });
        GivenTemplate(_realContainerId, _realTaskWithoutCloneId);

        await _service.ExpandAsync(ContainerWork(_cloneContainerId, _token), Day);

        _addedWorks.Select(work => work.ShiftId).ShouldBe([_cloneTaskId]);
        _addedWorks.ShouldAllBe(work => work.AnalyseToken == _token);
    }

    [Test]
    public async Task RealPlanWork_ExpandsItsOwnTemplate_WithoutReadingClones()
    {
        GivenTemplate(_realContainerId, _realTaskId);

        await _service.ExpandAsync(ContainerWork(_realContainerId, null), Day);

        _addedWorks.Select(work => work.ShiftId).ShouldBe([_realTaskId]);
        _addedWorks.ShouldAllBe(work => work.AnalyseToken == null);
        _addedChanges.ShouldAllBe(change => change.AnalyseToken == null);
        await _shiftReader.DidNotReceiveWithAnyArgs().GetScenarioClonesBySourceAsync(default!, default, default);
    }

    private void GivenTemplate(Guid containerId, params Guid[] taskShiftIds)
        => _templates.GetTemplatesForContainer(containerId).Returns(
        [
            new ContainerTemplate
            {
                ContainerId = containerId,
                Weekday = (int)Day.DayOfWeek,
                ContainerTemplateItems = taskShiftIds.Select(taskShiftId => new ContainerTemplateItem
                {
                    ShiftId = taskShiftId,
                    StartItem = new TimeOnly(8, 0),
                    EndItem = new TimeOnly(12, 0),
                    BriefingTime = Quarter,
                    DebriefingTime = Quarter,
                    TravelTimeBefore = Quarter,
                    TravelTimeAfter = Quarter
                }).ToList()
            }
        ]);

    private static Work ContainerWork(Guid containerShiftId, Guid? token)
        => new() { Id = Guid.NewGuid(), ShiftId = containerShiftId, ClientId = Guid.NewGuid(), CurrentDate = Day, AnalyseToken = token };
}
