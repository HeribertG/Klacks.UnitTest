// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// A corrected work (correction, travel or briefing WorkChange) is a fixed cell for every wizard, like a sealed work:
/// Wizard 1 locks it (locked token, never planned again, not a movable blocker), the harmonizer family (Wizard 2,
/// Holistic/Wizard 3, Wizard 4, AutoWizard - one context builder) locks its cell, both count its correction hours like
/// the period hours do, and the harmonizer apply never re-points or deletes it.
/// </summary>

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Schedules;
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
public class CorrectedWorkProtectionTests
{
    private static readonly DateOnly Day = new(2026, 11, 10);
    private static readonly DateOnly From = new(2026, 11, 9);
    private static readonly DateOnly Until = new(2026, 11, 15);
    private const decimal WorkHours = 8m;
    private const decimal CorrectionHours = 1.5m;

    private DataBaseContext _context = null!;
    private readonly Guid _agent = Guid.NewGuid();
    private readonly Guid _other = Guid.NewGuid();
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
    public void Types_EveryNonReplacementChangeIsACorrection_OnlyCorrectionAndTravelCarryHours()
    {
        CorrectionWorkChangeTypes.All.ShouldNotContain(WorkChangeType.ReplacementStart);
        CorrectionWorkChangeTypes.All.ShouldNotContain(WorkChangeType.ReplacementEnd);
        CorrectionWorkChangeTypes.All.ShouldNotContain(WorkChangeType.ReplacementWithin);
        CorrectionWorkChangeTypes.All.Count.ShouldBe(Enum.GetValues<WorkChangeType>().Length - ReplacementWorkChangeTypes.All.Count);
        CorrectionWorkChangeTypes.IsHourBearing(WorkChangeType.Briefing).ShouldBeFalse();
        CorrectionWorkChangeTypes.IsHourBearing(WorkChangeType.CorrectionEnd).ShouldBeTrue();
    }

    [Test]
    public async Task Query_LoadsCorrectedWorksWithTheirHours_AndIgnoresReplacements()
    {
        var corrected = AddWork(_agent, Day, null, WorkChangeType.CorrectionEnd);
        var briefed = AddWork(_agent, Day.AddDays(1), null, WorkChangeType.Briefing);
        var replaced = AddWork(_agent, Day.AddDays(2), null, WorkChangeType.ReplacementWithin, replaceClientId: _other);
        await _context.SaveChangesAsync();

        var hours = await CorrectedWorkQuery.LoadAsync(_context, null, From, Until, null, CancellationToken.None);

        hours[corrected.Id].ShouldBe(CorrectionHours);
        hours[briefed.Id].ShouldBe(0m, "a briefing fixes the work but adds no period hours");
        hours.ShouldNotContainKey(replaced.Id);
        (await CorrectedWorkQuery.LoadFixedWorkIdsAsync(_context, null, From, Until, CancellationToken.None))
            .ShouldBe(new[] { corrected.Id, briefed.Id, replaced.Id }, ignoreOrder: true);
    }

    [Test]
    public async Task Wizard1_CorrectedWorkBlocksMutation_IsALockedTokenWithItsCorrectionHours()
    {
        var corrected = AddWork(_agent, Day, null, WorkChangeType.CorrectionEnd);
        await _context.SaveChangesAsync();
        var keywordProvider = Substitute.For<IScheduleCommandKeywordProvider>();
        keywordProvider.GetAsync(Arg.Any<CancellationToken>()).Returns(ScheduleCommandKeywordTestFactory.Default);

        var result = await new WizardHardConstraintBuilder(_context, keywordProvider)
            .BuildAsync([_agent], From, Until, null, CancellationToken.None);

        var locked = result.LockedWorks.ShouldHaveSingleItem();
        locked.WorkId.ShouldBe(corrected.Id.ToString());
        locked.TotalHours.ShouldBe(WorkHours + CorrectionHours);
        result.ExistingWorkBlockers.ShouldNotContain(b => b.AgentId == _agent.ToString() && b.Date == Day);
    }

    [Test]
    public void HarmonizerFamily_CorrectedWorkBlocksMutation_CellIsLockedWithItsCorrectionHours()
    {
        var corrected = NewWork(_agent, Day, null);
        var plain = NewWork(_agent, Day.AddDays(1), null);

        var assignments = HarmonizerContextBuilder.BuildAssignments(
            [corrected, plain], [], [], new HashSet<Guid> { _agent },
            correctedWorkHours: new Dictionary<Guid, decimal> { [corrected.Id] = CorrectionHours });

        var fixedCell = assignments.Single(a => a.Date == Day);
        fixedCell.IsLocked.ShouldBeTrue();
        fixedCell.Hours.ShouldBe(WorkHours + CorrectionHours);
        assignments.Single(a => a.Date == Day.AddDays(1)).IsLocked.ShouldBeFalse();
    }

    [Test]
    public async Task HarmonizerApply_RepointNeverMovesOrDeletesACorrectedWork()
    {
        var token = Guid.NewGuid();
        var corrected = AddWork(_agent, Day, token, WorkChangeType.CorrectionEnd);
        await _context.SaveChangesAsync();
        var rows = new List<BitmapAgent>
        {
            new(_agent.ToString(), "A", 0m, new HashSet<CellSymbol>()),
            new(_other.ToString(), "B", 0m, new HashSet<CellSymbol>()),
        };
        var cells = new Cell[2, 1];
        cells[0, 0] = Cell.Free();
        cells[1, 0] = new Cell(CellSymbol.Early, _shift, new List<Guid> { corrected.Id }, false);

        await ApplyService().RepointClonedWorksAsync(
            token, From, Until, new HarmonyBitmap(rows, [Day], cells),
            new Dictionary<Guid, Guid> { [corrected.Id] = corrected.Id }, CancellationToken.None);
        await _context.SaveChangesAsync();

        var after = await _context.Work.IgnoreQueryFilters().AsNoTracking().SingleAsync(w => w.Id == corrected.Id);
        after.IsDeleted.ShouldBeFalse();
        after.ClientId.ShouldBe(_agent, "the correction is the agent's own time and never changes person");
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
        Klacks.UnitTest.TestHelpers.TestScenarioTokenAccess.AllVisible(),
        Substitute.For<ILogger<HarmonizerApplyService>>());

    private Work AddWork(Guid client, DateOnly date, Guid? token, WorkChangeType changeType, Guid? replaceClientId = null)
    {
        var work = NewWork(client, date, token);
        _context.Work.Add(work);
        _context.WorkChange.Add(new WorkChange
        {
            Id = Guid.NewGuid(),
            WorkId = work.Id,
            Type = changeType,
            ReplaceClientId = replaceClientId,
            StartTime = new TimeOnly(14, 0),
            EndTime = new TimeOnly(15, 30),
            ChangeTime = CorrectionHours,
            AnalyseToken = token,
        });
        return work;
    }

    private Work NewWork(Guid client, DateOnly date, Guid? token) => new()
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
    };
}
