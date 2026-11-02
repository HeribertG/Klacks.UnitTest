// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Briefing, debriefing and travel entries that ContainerWorkExpansionService creates are billable, for day overrides and weekly templates alike.
/// </summary>

using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Services.Schedules;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Services.Schedules;

[TestFixture]
public class ContainerWorkExpansionToInvoiceTests
{
    private static readonly DateOnly Day = new(2026, 11, 2);
    private static readonly TimeOnly Quarter = new(0, 15);

    private IContainerTemplateRepository _templates = null!;
    private IContainerShiftOverrideRepository _overrides = null!;
    private IWorkChangeRepository _workChanges = null!;
    private ContainerWorkExpansionService _service = null!;
    private Work _container = null!;
    private readonly List<WorkChange> _added = [];

    [SetUp]
    public void Setup()
    {
        _added.Clear();
        _templates = Substitute.For<IContainerTemplateRepository>();
        _overrides = Substitute.For<IContainerShiftOverrideRepository>();
        _workChanges = Substitute.For<IWorkChangeRepository>();
        _workChanges.When(r => r.Add(Arg.Any<WorkChange>())).Do(call => _added.Add(call.Arg<WorkChange>()));

        var shifts = Substitute.For<IShiftRepository>();
        _container = new Work { Id = Guid.NewGuid(), ShiftId = Guid.NewGuid(), ClientId = Guid.NewGuid(), CurrentDate = Day };
        shifts.Get(_container.ShiftId).Returns(new Shift { Id = _container.ShiftId, ShiftType = ShiftType.IsContainer });

        _service = new ContainerWorkExpansionService(
            _templates,
            _overrides,
            Substitute.For<IWorkRepository>(),
            Substitute.For<IBreakRepository>(),
            _workChanges,
            shifts,
            Substitute.For<IWorkWriteGuard>(),
            Substitute.For<ILogger<ContainerWorkExpansionService>>());
    }

    [Test]
    public async Task OverrideItem_CreatesFourBillableEntries()
    {
        _overrides.GetByContainerAndDateWithItems(_container.ShiftId, Day, Arg.Any<CancellationToken>())
            .Returns(new ContainerShiftOverride
            {
                ContainerShiftOverrideItems =
                [
                    new ContainerShiftOverrideItem
                    {
                        ShiftId = Guid.NewGuid(),
                        StartItem = new TimeOnly(8, 0),
                        EndItem = new TimeOnly(12, 0),
                        BriefingTime = Quarter,
                        DebriefingTime = Quarter,
                        TravelTimeBefore = Quarter,
                        TravelTimeAfter = Quarter
                    }
                ]
            });

        await _service.ExpandAsync(_container, Day);

        _added.Count.ShouldBe(4);
        _added.ShouldAllBe(wc => wc.ToInvoice);
    }

    [Test]
    public async Task TemplateItem_CreatesFourBillableEntries()
    {
        _templates.GetTemplatesForContainer(_container.ShiftId).Returns(
        [
            new ContainerTemplate
            {
                Weekday = (int)Day.DayOfWeek,
                ContainerTemplateItems =
                [
                    new ContainerTemplateItem
                    {
                        ShiftId = Guid.NewGuid(),
                        StartItem = new TimeOnly(8, 0),
                        EndItem = new TimeOnly(12, 0),
                        BriefingTime = Quarter,
                        DebriefingTime = Quarter,
                        TravelTimeBefore = Quarter,
                        TravelTimeAfter = Quarter
                    }
                ]
            }
        ]);

        await _service.ExpandAsync(_container, Day);

        _added.Count.ShouldBe(4);
        _added.ShouldAllBe(wc => wc.ToInvoice);
    }
}
