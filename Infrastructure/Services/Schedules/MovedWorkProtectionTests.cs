// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// A work a cover moved is a fixed cell for every wizard: Wizard 1 keeps it as a locked token, the harmonizer family
/// (Wizard 2, Holistic/Wizard 3, Wizard 4, AutoWizard - one context builder) locks its cell, the apply paths never touch it,
/// and it adds no hours on top of the work itself.
/// </summary>

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.Schedules;
using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.UnitTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules;

[TestFixture]
public class MovedWorkProtectionTests
{
    private static readonly DateOnly Day = new(2026, 11, 10);
    private static readonly DateOnly From = new(2026, 11, 9);
    private static readonly DateOnly Until = new(2026, 11, 15);
    private const decimal WorkHours = 8m;
    private const decimal CorrectionHours = 1.5m;

    private DataBaseContext _context = null!;
    private readonly Guid _agent = Guid.NewGuid();
    private readonly Guid _other = Guid.NewGuid();
    private readonly Guid _absent = Guid.NewGuid();
    private readonly Guid _shift = Guid.NewGuid();

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public async Task Query_LoadsAMovedWork_WithZeroHours_ALiveOneNot()
    {
        var moved = AddWork(_agent, Day, null, _absent);
        var plain = AddWork(_agent, Day.AddDays(1), null, null);
        await _context.SaveChangesAsync();

        var hours = await CorrectedWorkQuery.LoadAsync(_context, null, From, Until, null, CancellationToken.None);

        hours[moved.Id].ShouldBe(0m);
        hours.ShouldNotContainKey(plain.Id);
    }

    [Test]
    public async Task Query_KeepsTheCorrectionHours_OfAMovedWork()
    {
        var moved = AddWork(_agent, Day, null, _absent);
        _context.WorkChange.Add(new WorkChange
        {
            Id = Guid.NewGuid(),
            WorkId = moved.Id,
            Type = WorkChangeType.CorrectionEnd,
            StartTime = new TimeOnly(14, 0),
            EndTime = new TimeOnly(15, 30),
            ChangeTime = CorrectionHours,
        });
        await _context.SaveChangesAsync();

        var hours = await CorrectedWorkQuery.LoadAsync(_context, null, From, Until, null, CancellationToken.None);

        hours[moved.Id].ShouldBe(CorrectionHours);
    }

    [Test]
    public async Task Query_ReadsTheScenarioOfTheToken_AndTheAgentFilter()
    {
        var token = Guid.NewGuid();
        var inScenario = AddWork(_agent, Day, token, _absent);
        var real = AddWork(_agent, Day, null, _absent);
        await _context.SaveChangesAsync();

        (await CorrectedWorkQuery.LoadAsync(_context, token, From, Until, null, CancellationToken.None))
            .Keys.ShouldBe(new[] { inScenario.Id });
        (await CorrectedWorkQuery.LoadAsync(_context, null, From, Until, [_other], CancellationToken.None))
            .ShouldBeEmpty();
        (await CorrectedWorkQuery.LoadFixedWorkIdsAsync(_context, null, From, Until, CancellationToken.None))
            .ShouldBe(new[] { real.Id });
    }

    [Test]
    public async Task Wizard1_MovedWorkBlocksMutation_IsALockedTokenWithItsOwnHours()
    {
        var moved = AddWork(_agent, Day, null, _absent);
        await _context.SaveChangesAsync();
        var keywordProvider = Substitute.For<IScheduleCommandKeywordProvider>();
        keywordProvider.GetAsync(Arg.Any<CancellationToken>()).Returns(ScheduleCommandKeywordTestFactory.Default);

        var result = await new WizardHardConstraintBuilder(_context, keywordProvider)
            .BuildAsync([_agent], From, Until, null, CancellationToken.None);

        var locked = result.LockedWorks.ShouldHaveSingleItem();
        locked.WorkId.ShouldBe(moved.Id.ToString());
        locked.TotalHours.ShouldBe(WorkHours);
        result.ExistingWorkBlockers.ShouldNotContain(b => b.AgentId == _agent.ToString() && b.Date == Day);
    }

    [Test]
    public async Task HarmonizerFamily_MovedWorkBlocksMutation_CellIsLockedWithItsOwnHours()
    {
        var moved = AddWork(_agent, Day, null, _absent);
        var plain = AddWork(_agent, Day.AddDays(1), null, null);
        await _context.SaveChangesAsync();
        var fixedWorks = await CorrectedWorkQuery.LoadAsync(_context, null, From, Until, null, CancellationToken.None);

        var assignments = HarmonizerContextBuilder.BuildAssignments(
            [moved, plain], [], [], new HashSet<Guid> { _agent }, correctedWorkHours: fixedWorks);

        var fixedCell = assignments.Single(a => a.Date == Day);
        fixedCell.IsLocked.ShouldBeTrue();
        fixedCell.Hours.ShouldBe(WorkHours);
        assignments.Single(a => a.Date == Day.AddDays(1)).IsLocked.ShouldBeFalse();
    }

    [Test]
    public async Task HarmonizerApply_RepointNeverMovesOrDeletesAMovedWork()
    {
        var token = Guid.NewGuid();
        var moved = AddWork(_agent, Day, token, _absent);
        await _context.SaveChangesAsync();
        var rows = new List<BitmapAgent>
        {
            new(_agent.ToString(), "A", 0m, new HashSet<CellSymbol>()),
            new(_other.ToString(), "B", 0m, new HashSet<CellSymbol>()),
        };
        var cells = new Cell[2, 1];
        cells[0, 0] = Cell.Free();
        cells[1, 0] = new Cell(CellSymbol.Early, _shift, new List<Guid> { moved.Id }, false);

        await ApplyService().RepointClonedWorksAsync(
            token, From, Until, new HarmonyBitmap(rows, [Day], cells),
            new Dictionary<Guid, Guid> { [moved.Id] = moved.Id }, CancellationToken.None);
        await _context.SaveChangesAsync();

        var after = await _context.Work.IgnoreQueryFilters().AsNoTracking().SingleAsync(w => w.Id == moved.Id);
        after.IsDeleted.ShouldBeFalse();
        after.ClientId.ShouldBe(_agent);
    }

    private HarmonizerApplyService ApplyService() => new(
        new HarmonizerResultCache(),
        Substitute.For<IMediator>(),
        Substitute.For<IAnalyseScenarioRepository>(),
        Substitute.For<IAnalyseScenarioService>(),
        new UnitOfWork(_context, Substitute.For<ILogger<UnitOfWork>>()),
        _context,
        Substitute.For<IWizardRunCaptureRepository>(),
        Substitute.For<IScenarioComplianceService>(),
        Substitute.For<IScheduleTimelineService>(),
        Substitute.For<IScheduleSnapshotMarkerService>(),
        Substitute.For<ICompanyClock>(),
        Substitute.For<IScenarioNameGenerator>(),
        TestScenarioTokenAccess.AllVisible(),
        Substitute.For<ILogger<HarmonizerApplyService>>());

    private Work AddWork(Guid client, DateOnly date, Guid? token, Guid? movedFrom)
    {
        var work = new Work
        {
            Id = Guid.NewGuid(),
            ClientId = client,
            ShiftId = _shift,
            CurrentDate = date,
            StartTime = new TimeOnly(6, 0),
            EndTime = new TimeOnly(14, 0),
            WorkTime = WorkHours,
            LockLevel = WorkLockLevel.None,
            AnalyseToken = token,
            MovedFromClientId = movedFrom,
        };
        _context.Work.Add(work);
        return work;
    }
}
