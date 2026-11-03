// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// A sub-break the expansion creates is calculated like the recalculation of its container before it is added, for day
/// overrides and weekly templates alike, so a later change of owner leaves the hours as they are.
/// </summary>

using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Services.Schedules;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Services.Schedules;

[TestFixture]
public class ContainerWorkExpansionSubBreakTests
{
    private static readonly DateOnly Day = new(2026, 11, 2);
    private static readonly decimal UnpaidHours = -1m;

    private IContainerTemplateRepository _templates = null!;
    private IContainerShiftOverrideRepository _overrides = null!;
    private IBreakRepository _breaks = null!;
    private IContainerWorkChildrenManager _childrenManager = null!;
    private ContainerWorkExpansionService _service = null!;
    private Work _container = null!;
    private readonly List<decimal> _addedWorkTimes = [];

    [SetUp]
    public void Setup()
    {
        _addedWorkTimes.Clear();
        _templates = Substitute.For<IContainerTemplateRepository>();
        _overrides = Substitute.For<IContainerShiftOverrideRepository>();
        _breaks = Substitute.For<IBreakRepository>();
        _breaks.When(r => r.Add(Arg.Any<Break>())).Do(call => _addedWorkTimes.Add(call.Arg<Break>().WorkTime));
        _childrenManager = Substitute.For<IContainerWorkChildrenManager>();
        _childrenManager.CalculateNewSubBreakAsync(Arg.Any<Break>(), Arg.Any<Work>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                call.Arg<Break>().WorkTime = UnpaidHours;
                return true;
            });

        _container = new Work { Id = Guid.NewGuid(), ShiftId = Guid.NewGuid(), ClientId = Guid.NewGuid(), CurrentDate = Day };
        var shifts = Substitute.For<IShiftRepository>();
        shifts.Get(_container.ShiftId).Returns(new Shift { Id = _container.ShiftId, ShiftType = ShiftType.IsContainer });

        _service = new ContainerWorkExpansionService(
            _templates,
            _overrides,
            Substitute.For<IWorkRepository>(),
            _breaks,
            Substitute.For<IWorkChangeRepository>(),
            shifts,
            Substitute.For<IWorkWriteGuard>(),
            _childrenManager,
            Substitute.For<ILogger<ContainerWorkExpansionService>>());
    }

    [Test]
    public async Task ATemplateSubBreak_IsCalculatedBeforeItIsAdded()
    {
        _templates.GetTemplatesForContainer(_container.ShiftId).Returns(
        [
            new ContainerTemplate
            {
                Weekday = (int)Day.DayOfWeek,
                ContainerTemplateItems =
                [
                    new ContainerTemplateItem { AbsenceId = Guid.NewGuid(), StartItem = new TimeOnly(12, 0), EndItem = new TimeOnly(13, 0) }
                ]
            }
        ]);

        await _service.ExpandAsync(_container, Day);

        _addedWorkTimes.ShouldBe([UnpaidHours]);
        await _childrenManager.Received(1).CalculateNewSubBreakAsync(
            Arg.Is<Break>(b => b.ParentWorkId == _container.Id), _container, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AnOverrideSubBreak_IsCalculatedBeforeItIsAdded()
    {
        _overrides.GetByContainerAndDateWithItems(_container.ShiftId, Day, Arg.Any<CancellationToken>())
            .Returns(new ContainerShiftOverride
            {
                ContainerShiftOverrideItems =
                [
                    new ContainerShiftOverrideItem { AbsenceId = Guid.NewGuid(), StartItem = new TimeOnly(12, 0), EndItem = new TimeOnly(13, 0) }
                ]
            });

        await _service.ExpandAsync(_container, Day);

        _addedWorkTimes.ShouldBe([UnpaidHours]);
    }
}
