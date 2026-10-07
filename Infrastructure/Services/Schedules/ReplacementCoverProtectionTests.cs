// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// A work handed to a substitute by a replacement WorkChange (absence recovery or a planner's manual replacement)
/// must survive every wizard: the work stays fixed and the substitute counts as occupied for the replaced span.
/// Before 2026-10-07 the wizards only read Work.LockLevel, so a later run could delete the cover together with its
/// WorkChange and double-book the substitute.
/// </summary>

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Models.Schedules;
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
public class ReplacementCoverProtectionTests
{
    private static readonly DateOnly Day = new(2026, 11, 10);
    private static readonly DateOnly From = new(2026, 11, 9);
    private static readonly DateOnly Until = new(2026, 11, 15);

    private DataBaseContext _context = null!;
    private Guid _absent;
    private Guid _substitute;
    private Guid _shift;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _absent = Guid.NewGuid();
        _substitute = Guid.NewGuid();
        _shift = Guid.NewGuid();
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [TestCase(WorkChangeType.ReplacementWithin, 7, 11, 7, 11)]
    [TestCase(WorkChangeType.ReplacementStart, 0, 0, 6, 10)]
    [TestCase(WorkChangeType.ReplacementEnd, 0, 0, 10, 14)]
    public async Task Query_LoadsEveryReplacementTypeWithItsEffectiveWindow(
        WorkChangeType type, int storedStart, int storedEnd, int expectedStart, int expectedEnd)
    {
        var work = AddWork(_absent, Day, token: null);
        AddChange(work, type, _substitute, token: null, new TimeOnly(storedStart, 0), new TimeOnly(storedEnd, 0));
        await _context.SaveChangesAsync();

        var covers = await ReplacementCoverQuery.LoadAsync(_context, null, From, Until, null, CancellationToken.None);

        var cover = covers.ShouldHaveSingleItem();
        cover.WorkId.ShouldBe(work.Id);
        cover.SubstituteClientId.ShouldBe(_substitute);
        cover.Hours.ShouldBe(4m);
        cover.StartAt.ShouldBe(Day.ToDateTime(new TimeOnly(expectedStart, 0)));
        cover.EndAt.ShouldBe(Day.ToDateTime(new TimeOnly(expectedEnd, 0)));
    }

    [Test]
    public void Window_WithinNightShiftAfterMidnight_LiesOnTheNextDay()
    {
        var (startAt, endAt) = ReplacementWindow.ToInterval(Day, new TimeOnly(22, 0), new TimeOnly(1, 0), new TimeOnly(5, 0));

        startAt.ShouldBe(Day.AddDays(1).ToDateTime(new TimeOnly(1, 0)));
        endAt.ShouldBe(Day.AddDays(1).ToDateTime(new TimeOnly(5, 0)));
    }

    [Test]
    public void Window_EndOfNightShift_EndsNextDay()
    {
        var (start, end) = ReplacementWindow.Compute(
            WorkChangeType.ReplacementEnd, new TimeOnly(22, 0), new TimeOnly(6, 0), default, default, 3m);
        var (startAt, endAt) = ReplacementWindow.ToInterval(Day, new TimeOnly(22, 0), start, end);

        start.ShouldBe(new TimeOnly(3, 0));
        startAt.ShouldBe(Day.AddDays(1).ToDateTime(new TimeOnly(3, 0)));
        endAt.ShouldBe(Day.AddDays(1).ToDateTime(new TimeOnly(6, 0)));
    }

    [TestCase(WorkChangeType.CorrectionEnd)]
    [TestCase(WorkChangeType.TravelWithin)]
    [TestCase(WorkChangeType.Briefing)]
    public async Task Query_IgnoresNonReplacementTypes(WorkChangeType type)
    {
        var work = AddWork(_absent, Day, token: null);
        AddChange(work, type, _substitute, token: null);
        await _context.SaveChangesAsync();

        (await ReplacementCoverQuery.LoadAsync(_context, null, From, Until, null, CancellationToken.None)).ShouldBeEmpty();
    }

    [Test]
    public async Task Query_RespectsScenarioTokenAndAgentFilter()
    {
        var token = Guid.NewGuid();
        var real = AddWork(_absent, Day, token: null);
        AddChange(real, WorkChangeType.ReplacementWithin, _substitute, token: null);
        var scenario = AddWork(_absent, Day, token);
        AddChange(scenario, WorkChangeType.ReplacementWithin, _substitute, token);
        await _context.SaveChangesAsync();

        var scenarioCovers = await ReplacementCoverQuery.LoadAsync(_context, token, From, Until, null, CancellationToken.None);
        var bySubstitute = await ReplacementCoverQuery.LoadAsync(_context, null, From, Until, [_substitute], CancellationToken.None);
        var byStranger = await ReplacementCoverQuery.LoadAsync(_context, null, From, Until, [Guid.NewGuid()], CancellationToken.None);

        scenarioCovers.Single().WorkId.ShouldBe(scenario.Id);
        bySubstitute.Single().WorkId.ShouldBe(real.Id);
        byStranger.ShouldBeEmpty();
    }

    [Test]
    public async Task Query_ChangeTimeLongerThanTheWork_IsClampedToTheShift()
    {
        var work = AddWork(_absent, Day, token: null);
        var change = AddChange(work, WorkChangeType.ReplacementEnd, _substitute, token: null, default, default);
        change.ChangeTime = 9m;
        await _context.SaveChangesAsync();

        var cover = (await ReplacementCoverQuery.LoadAsync(_context, null, From, Until, null, CancellationToken.None)).ShouldHaveSingleItem();

        cover.Hours.ShouldBe(8m);
        cover.StartAt.ShouldBe(Day.ToDateTime(new TimeOnly(6, 0)));
        cover.EndAt.ShouldBe(Day.ToDateTime(new TimeOnly(14, 0)));
    }

    [Test]
    public async Task Query_ZeroChangeTime_IsNoOccupancy()
    {
        var work = AddWork(_absent, Day, token: null);
        var change = AddChange(work, WorkChangeType.ReplacementStart, _substitute, token: null, default, default);
        change.ChangeTime = 0m;
        await _context.SaveChangesAsync();

        (await ReplacementCoverQuery.LoadAsync(_context, null, From, Until, null, CancellationToken.None)).ShouldBeEmpty();
    }

    [Test]
    public void Window_WorkLengthOfANightShift_WrapsMidnight()
    {
        ReplacementWindow.WorkLengthHours(new TimeOnly(22, 0), new TimeOnly(6, 0)).ShouldBe(8m);
        ReplacementWindow.ClampHours(new TimeOnly(22, 0), new TimeOnly(6, 0), 9m).ShouldBe(8m);
    }

    [Test]
    public void BuildAssignments_LocksCoveredWorkAndOccupiesSubstitute()    {
        var work = Work(_absent, Day, token: null);
        var cover = Cover(work.Id);

        var assignments = HarmonizerContextBuilder.BuildAssignments([work], [], [cover], new HashSet<Guid> { _absent, _substitute });

        var original = assignments.Single(a => a.AgentId == _absent.ToString());
        original.IsLocked.ShouldBeTrue();
        original.Hours.ShouldBe(4m, "the handed-over hours are not counted twice");
        var substitute = assignments.Single(a => a.AgentId == _substitute.ToString());
        substitute.IsLocked.ShouldBeTrue();
        substitute.WorkIds.ShouldBeEmpty();
        substitute.Hours.ShouldBe(4m);
        substitute.Date.ShouldBe(Day);
        substitute.Symbol.ShouldBe(CellSymbol.Early);
    }

    [Test]
    public void BuildAssignments_SubstituteOutsideTheRun_IsNotAdded()
    {
        var work = Work(_absent, Day, token: null);
        var cover = Cover(work.Id);

        var assignments = HarmonizerContextBuilder.BuildAssignments([work], [], [cover], new HashSet<Guid> { _absent });

        assignments.Count.ShouldBe(1);
        assignments[0].IsLocked.ShouldBeTrue();
    }

    [Test]
    public void BuildAssignments_UncoveredWork_StaysMovable()
    {
        var assignments = HarmonizerContextBuilder.BuildAssignments([Work(_absent, Day, token: null)], [], [], new HashSet<Guid> { _absent });

        assignments.Single().IsLocked.ShouldBeFalse();
    }

    [Test]
    public async Task Wizard1_CoveredWorkIsLockedAndSubstituteIsBlocked()
    {
        var covered = AddWork(_absent, Day, token: null);
        AddChange(covered, WorkChangeType.ReplacementStart, _substitute, token: null, default, default);
        var corrected = AddWork(_absent, Day.AddDays(1), token: null);
        AddChange(corrected, WorkChangeType.CorrectionEnd, null, token: null);
        await _context.SaveChangesAsync();
        var keywordProvider = Substitute.For<IScheduleCommandKeywordProvider>();
        keywordProvider.GetAsync(Arg.Any<CancellationToken>()).Returns(ScheduleCommandKeywordTestFactory.Default);
        var builder = new WizardHardConstraintBuilder(_context, keywordProvider);

        var result = await builder.BuildAsync([_absent, _substitute], From, Until, null, CancellationToken.None);

        result.LockedWorks.Select(w => w.WorkId).ShouldBe([covered.Id.ToString()]);
        result.LockedWorks[0].TotalHours.ShouldBe(4m);
        var substituteBlocker = result.ExistingWorkBlockers.Single(b => b.AgentId == _substitute.ToString());
        substituteBlocker.StartAt.ShouldBe(Day.ToDateTime(new TimeOnly(6, 0)));
        substituteBlocker.EndAt.ShouldBe(Day.ToDateTime(new TimeOnly(10, 0)));
        result.ExistingWorkBlockers.ShouldNotContain(b => b.AgentId == _absent.ToString() && b.Date == Day);
        result.ExistingWorkBlockers.ShouldContain(b => b.AgentId == _absent.ToString() && b.Date == Day.AddDays(1));
    }

    [Test]
    public async Task Apply_RepointKeepsCoveredWorkAndItsReplacement()
    {
        var token = Guid.NewGuid();
        var covered = AddWork(_absent, Day, token);
        var change = AddChange(covered, WorkChangeType.ReplacementWithin, _substitute, token);
        var movable = AddWork(_absent, Day.AddDays(1), token);
        var substituteOwn = AddWork(_substitute, Day, token);
        await _context.SaveChangesAsync();

        var rows = new List<BitmapAgent>
        {
            new(_absent.ToString(), "A", 0m, new HashSet<CellSymbol>()),
            new(_substitute.ToString(), "S", 0m, new HashSet<CellSymbol>()),
        };
        var days = new List<DateOnly> { Day, Day.AddDays(1) };
        var cells = new Cell[2, 2];
        cells[0, 0] = new Cell(CellSymbol.Early, _shift, new List<Guid> { covered.Id }, true);
        cells[0, 1] = Cell.Free();
        cells[1, 0] = new Cell(CellSymbol.Early, _shift, new List<Guid> { substituteOwn.Id }, true);
        cells[1, 1] = new Cell(CellSymbol.Early, _shift, new List<Guid> { movable.Id }, false);
        var bitmap = new HarmonyBitmap(rows, days, cells);
        var identity = new Dictionary<Guid, Guid>
        {
            [covered.Id] = covered.Id, [movable.Id] = movable.Id, [substituteOwn.Id] = substituteOwn.Id,
        };

        await ApplyService().RepointClonedWorksAsync(token, From, Until, bitmap, identity, CancellationToken.None);
        await _context.SaveChangesAsync();

        var coveredAfter = await _context.Work.IgnoreQueryFilters().AsNoTracking().SingleAsync(w => w.Id == covered.Id);
        coveredAfter.IsDeleted.ShouldBeFalse("a covered work must never be deleted by a wizard apply");
        coveredAfter.ClientId.ShouldBe(_absent);
        var changeAfter = await _context.WorkChange.IgnoreQueryFilters().AsNoTracking().SingleAsync(c => c.Id == change.Id);
        changeAfter.IsDeleted.ShouldBeFalse("the replacement itself must survive");
        var movableAfter = await _context.Work.IgnoreQueryFilters().AsNoTracking().SingleAsync(w => w.Id == movable.Id);
        movableAfter.ClientId.ShouldBe(_substitute, "uncovered works are still re-pointed as before");
        var ownAfter = await _context.Work.IgnoreQueryFilters().AsNoTracking().SingleAsync(w => w.Id == substituteOwn.Id);
        ownAfter.IsDeleted.ShouldBeFalse("an unlocked work merged into a locked cell did not move and must survive");
    }

    [Test]
    public async Task Wizard1Apply_SlotDeleteKeepsCoveredWork()
    {
        var token = Guid.NewGuid();
        var covered = AddWork(_absent, Day, token);
        AddChange(covered, WorkChangeType.ReplacementWithin, _substitute, token);
        var plain = AddWork(_substitute, Day, token);
        await _context.SaveChangesAsync();
        var service = new Klacks.Api.Infrastructure.Services.AnalyseScenarios.AnalyseScenarioService(_context);

        await service.SoftDeleteClonedWorksOnSlotsAsync(token, From, Until, new HashSet<(Guid, DateOnly)> { (_shift, Day) }, CancellationToken.None);
        await _context.SaveChangesAsync();

        (await _context.Work.IgnoreQueryFilters().AsNoTracking().SingleAsync(w => w.Id == covered.Id)).IsDeleted
            .ShouldBeFalse("the covered work keeps its slot");
        (await _context.Work.IgnoreQueryFilters().AsNoTracking().SingleAsync(w => w.Id == plain.Id)).IsDeleted
            .ShouldBeTrue("an uncovered work on a planned slot is still replaced as before");
    }

    private HarmonizerApplyService ApplyService() => new(        new HarmonizerResultCache(),
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
        Substitute.For<ILogger<HarmonizerApplyService>>());

    private Work AddWork(Guid client, DateOnly date, Guid? token)
    {
        var work = Work(client, date, token);
        _context.Work.Add(work);
        return work;
    }

    private Work Work(Guid client, DateOnly date, Guid? token) => new()
    {
        Id = Guid.NewGuid(),
        ClientId = client,
        ShiftId = _shift,
        CurrentDate = date,
        StartTime = new TimeOnly(6, 0),
        EndTime = new TimeOnly(14, 0),
        WorkTime = 8m,
        LockLevel = WorkLockLevel.None,
        AnalyseToken = token,
    };

    private ReplacementCover Cover(Guid workId) => new(
        workId, _absent, _substitute, _shift, Day, new TimeOnly(6, 0), new TimeOnly(10, 0),
        Day.ToDateTime(new TimeOnly(6, 0)), Day.ToDateTime(new TimeOnly(10, 0)), 4m);

    private WorkChange AddChange(Work work, WorkChangeType type, Guid? replaceClientId, Guid? token) =>
        AddChange(work, type, replaceClientId, token, new TimeOnly(6, 0), new TimeOnly(10, 0));

    private WorkChange AddChange(Work work, WorkChangeType type, Guid? replaceClientId, Guid? token, TimeOnly start, TimeOnly end)
    {
        var change = new WorkChange
        {
            Id = Guid.NewGuid(),
            WorkId = work.Id,
            Type = type,
            ReplaceClientId = replaceClientId,
            StartTime = start,
            EndTime = end,
            ChangeTime = 4m,
            AnalyseToken = token,
        };
        _context.WorkChange.Add(change);
        return change;
    }
}
