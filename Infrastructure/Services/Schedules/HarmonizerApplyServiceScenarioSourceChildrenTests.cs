// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Harmonizer apply with a SCENARIO as source, wired to the real AnalyseScenarioService clone: a movable work the
/// harmonizer hands to another agent keeps its expenses (taxable and non-taxable) in the new scenario and after accept;
/// a corrected work is fixed and stays with its employee together with its correction (payroll export must never lose an expense). A bitmap day outside the source
/// scenario's range (the clone takes the real plan there) is correlated through Work.ClonedFromWorkId and keeps the
/// real work's expense; a legacy source without that link is correlated by key (client, root shift, date, times) when the
/// match is unique and otherwise materialises the work from the bitmap. A real work moved after the source scenario was
/// made keeps its real-plan state (and expense) instead of being pulled back to the bitmap's date.
/// </summary>

using Klacks.Api.Application.Commands.Works;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.Api.Infrastructure.Services.AnalyseScenarios;
using Klacks.Api.Infrastructure.Services.Schedules;
using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.UnitTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules;

[TestFixture]
public class HarmonizerApplyServiceScenarioSourceChildrenTests
{
    private static readonly DateOnly Day = new(2026, 3, 10);
    private static readonly DateOnly SourceUntil = Day.AddDays(6);
    private static readonly DateOnly OutsideSourceDay = SourceUntil.AddDays(3);
    private static readonly Guid AgentA = Guid.NewGuid();
    private static readonly Guid AgentB = Guid.NewGuid();
    private const decimal NonTaxableAmount = 12.5m;
    private const decimal TaxableAmount = 40m;
    private const decimal BoundaryAmount = 7m;

    private DataBaseContext _context = null!;
    private AnalyseScenarioService _scenarioService = null!;
    private HarmonizerResultCache _cache = null!;
    private IMediator _mediator = null!;
    private readonly List<BulkAddWorksCommand> _bulkCommands = [];
    private Guid _realShiftId;
    private Guid _sourceToken;
    private Work _realInRangeWork = null!;
    private Work _realBoundaryWork = null!;
    private Work _sourceInRangeWork = null!;
    private Work _sourceBoundaryWork = null!;

    [SetUp]
    public async Task SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _scenarioService = new AnalyseScenarioService(_context);
        _cache = new HarmonizerResultCache();
        _bulkCommands.Clear();

        _realShiftId = Guid.NewGuid();
        _context.Shift.Add(new Shift { Id = _realShiftId, Name = "Early" });
        _realInRangeWork = AddRealWork(Day);
        _realBoundaryWork = AddRealWork(OutsideSourceDay);
        _context.Expenses.Add(new Expenses
        {
            Id = Guid.NewGuid(), WorkId = _realBoundaryWork.Id, Amount = BoundaryAmount, Taxable = false,
            Description = "Parking",
        });
        await _context.SaveChangesAsync();

        _sourceToken = Guid.NewGuid();
        await _scenarioService.CloneScenarioDataWithMapsAsync(null, Day, SourceUntil, _sourceToken, null, null, CancellationToken.None);
        _context.Set<AnalyseScenario>().Add(new AnalyseScenario
        {
            Id = Guid.NewGuid(),
            Name = "Source",
            Token = _sourceToken,
            FromDate = Day,
            UntilDate = SourceUntil,
            Status = AnalyseScenarioStatus.Active,
            RunGroupId = Guid.NewGuid(),
        });
        await _context.SaveChangesAsync();

        var sourceWorks = await _context.Work.Where(w => w.AnalyseToken == _sourceToken).ToListAsync();
        _sourceInRangeWork = sourceWorks.Single(w => w.CurrentDate == Day);
        _sourceBoundaryWork = sourceWorks.Single(w => w.CurrentDate == OutsideSourceDay);

        _context.Expenses.Add(new Expenses
        {
            Id = Guid.NewGuid(), WorkId = _sourceInRangeWork.Id, Amount = NonTaxableAmount, Taxable = false,
            Description = "Train ticket", AnalyseToken = _sourceToken,
        });
        _context.Expenses.Add(new Expenses
        {
            Id = Guid.NewGuid(), WorkId = _sourceInRangeWork.Id, Amount = TaxableAmount, Taxable = true,
            Description = "Allowance", AnalyseToken = _sourceToken,
        });
        await _context.SaveChangesAsync();
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public async Task ApplyFromScenario_ReassignedWorkKeepsItsExpenses()
    {
        var token = await ApplyAsync();

        var work = (await LiveWorksOf(token)).Where(w => w.CurrentDate == Day).ToList().ShouldHaveSingleItem();
        work.ClientId.ShouldBe(AgentB, "the harmonizer handed the work to agent B");

        var expenses = await _context.Expenses.Where(e => e.WorkId == work.Id && !e.IsDeleted).ToListAsync();
        expenses.Count.ShouldBe(2);
        expenses.ShouldAllBe(e => e.AnalyseToken == token);
        expenses.Single(e => !e.Taxable).Amount.ShouldBe(NonTaxableAmount);
        expenses.Single(e => e.Taxable).Amount.ShouldBe(TaxableAmount);

        _bulkCommands.SelectMany(c => c.Request.Works).ShouldNotContain(
            i => i.CurrentDate == Day, "an in-range work is re-pointed, never recreated without its children");
    }

    [Test]
    public async Task ApplyFromScenario_CorrectedWorkBlocksMutation_StaysWithItsEmployeeAndCorrection()
    {
        _context.WorkChange.Add(new WorkChange
        {
            Id = Guid.NewGuid(), WorkId = _sourceInRangeWork.Id, Type = WorkChangeType.CorrectionEnd,
            StartTime = new TimeOnly(16, 0), EndTime = new TimeOnly(17, 0), ChangeTime = 1m,
            Description = "Overtime", AnalyseToken = _sourceToken,
        });
        await _context.SaveChangesAsync();

        var token = await ApplyAsync();

        var work = (await LiveWorksOf(token)).Where(w => w.CurrentDate == Day).ToList().ShouldHaveSingleItem();
        work.ClientId.ShouldBe(AgentA, "a corrected work is fixed: its recorded time never moves to another employee");
        var change = await _context.WorkChange.SingleAsync(wc => wc.WorkId == work.Id && !wc.IsDeleted);
        change.Type.ShouldBe(WorkChangeType.CorrectionEnd);
        (await _context.Expenses.CountAsync(e => e.WorkId == work.Id && !e.IsDeleted)).ShouldBe(2);
    }

    [Test]
    public async Task ApplyFromScenario_DayOutsideTheSourceRange_IsRepointedThroughClonedFromWorkId_AndKeepsItsExpense()
    {
        var token = await ApplyAsync();

        _bulkCommands.SelectMany(c => c.Request.Works).ShouldBeEmpty("every bitmap work is correlated, nothing is recreated");
        var work = (await LiveWorksOf(token)).Where(w => w.CurrentDate == OutsideSourceDay).ToList().ShouldHaveSingleItem();
        work.ClientId.ShouldBe(AgentB);
        work.ClonedFromWorkId.ShouldBe(_realBoundaryWork.Id);
        var expense = await _context.Expenses.SingleAsync(e => e.WorkId == work.Id && !e.IsDeleted);
        expense.Amount.ShouldBe(BoundaryAmount);
        expense.AnalyseToken.ShouldBe(token);
    }

    [Test]
    public async Task ApplyFromScenario_LegacySourceWithoutClonedFromWorkId_IsCorrelatedByKey_AndKeepsItsExpense()
    {
        await ClearLegacyLinkAsync();

        var token = await ApplyAsync();

        _bulkCommands.SelectMany(c => c.Request.Works).ShouldBeEmpty("a unique key match re-points instead of recreating");
        var work = (await LiveWorksOf(token)).Where(w => w.CurrentDate == OutsideSourceDay).ToList().ShouldHaveSingleItem();
        work.ClientId.ShouldBe(AgentB);
        var expense = await _context.Expenses.SingleAsync(e => e.WorkId == work.Id && !e.IsDeleted);
        expense.Amount.ShouldBe(BoundaryAmount);
    }

    [Test]
    public async Task ApplyFromScenario_LegacySourceWithAnAmbiguousKey_RecreatesTheBoundaryWorkFromTheBitmap()
    {
        await ClearLegacyLinkAsync();
        AddRealWork(OutsideSourceDay);
        await _context.SaveChangesAsync();

        var token = await ApplyAsync();

        var item = _bulkCommands.SelectMany(c => c.Request.Works).Where(i => i.CurrentDate == OutsideSourceDay).ToList().ShouldHaveSingleItem();
        item.ClientId.ShouldBe(AgentB);
        item.AnalyseToken.ShouldBe(token);
        _bulkCommands.ShouldAllBe(c => c.PeriodCheck == PlanningPeriodCheck.SkipForPlannerOutput);
        (await LiveWorksOf(token)).ShouldNotContain(
            w => w.CurrentDate == OutsideSourceDay, "the real-plan clones of that day are replaced by the bitmap's work, not doubled");
    }

    [Test]
    public async Task ApplyFromScenario_RealWorkMovedAfterTheSourceScenario_KeepsTheRealPlanState_AndItsExpense()
    {
        var movedDay = SourceUntil.AddDays(1);
        var real = await _context.Work.SingleAsync(w => w.Id == _realBoundaryWork.Id);
        real.CurrentDate = movedDay;
        await _context.SaveChangesAsync();

        var token = await ApplyAsync();

        var clone = (await LiveWorksOf(token)).Where(w => w.CurrentDate == movedDay).ToList().ShouldHaveSingleItem();
        clone.ClientId.ShouldBe(AgentA, "the real plan moved the work after the source scenario was made; that state wins");
        clone.ClonedFromWorkId.ShouldBe(_realBoundaryWork.Id);
        (await _context.Expenses.SingleAsync(e => e.WorkId == clone.Id && !e.IsDeleted)).Amount.ShouldBe(BoundaryAmount);
        _bulkCommands.SelectMany(c => c.Request.Works).ShouldNotContain(i => i.CurrentDate == OutsideSourceDay);
        (await LiveWorksOf(token)).ShouldNotContain(w => w.CurrentDate == OutsideSourceDay);
    }

    private async Task ClearLegacyLinkAsync()
    {
        var legacyCopy = await _context.Work.SingleAsync(w => w.Id == _sourceBoundaryWork.Id);
        legacyCopy.ClonedFromWorkId = null;
        await _context.SaveChangesAsync();
    }

    [Test]
    public async Task Clone_RecordsTheSourceWorkOfEveryClonedWork_IncludingSubWorks()
    {
        _sourceInRangeWork.ClonedFromWorkId.ShouldBe(_realInRangeWork.Id);
        _sourceBoundaryWork.ClonedFromWorkId.ShouldBe(_realBoundaryWork.Id);

        var date = Day.AddDays(1);
        var parent = AddRealWork(date);
        var child = AddRealWork(date, parent.Id);
        await _context.SaveChangesAsync();
        var token = Guid.NewGuid();

        await _scenarioService.CloneScenarioDataWithMapsAsync(null, date, date, token, null, null, CancellationToken.None);
        await _context.SaveChangesAsync();

        var clones = (await LiveWorksOf(token)).Where(w => w.CurrentDate == date).ToList();
        clones.Single(w => w.ParentWorkId == null).ClonedFromWorkId.ShouldBe(parent.Id);
        clones.Single(w => w.ParentWorkId != null).ClonedFromWorkId.ShouldBe(child.Id);
    }

    [Test]
    public async Task ApplyFromScenario_ThenAccept_ExpensesReachTheRealPlan()
    {
        var token = await ApplyAsync();

        await _scenarioService.ValidateNoAcceptConflictsAsync(token, CancellationToken.None);
        await _scenarioService.SoftDeleteRealScheduleDataAsync(null, token, Day, OutsideSourceDay, CancellationToken.None);
        await _scenarioService.PromoteScenarioWorksAsync(token, Day, OutsideSourceDay, CancellationToken.None);
        await _context.SaveChangesAsync();

        var realWork = await _context.Work.SingleAsync(w => !w.IsDeleted && w.AnalyseToken == null && w.CurrentDate == Day);
        realWork.ClientId.ShouldBe(AgentB);
        realWork.ShiftId.ShouldBe(_realShiftId);
        realWork.ClonedFromWorkId.ShouldBeNull("a real work is not a clone");

        var expenses = await _context.Expenses.Where(e => e.WorkId == realWork.Id && !e.IsDeleted).ToListAsync();
        expenses.Count.ShouldBe(2);
        expenses.ShouldAllBe(e => e.AnalyseToken == null);
        expenses.Select(e => e.Taxable).ShouldBe(new[] { false, true }, ignoreOrder: true);

        var realBoundary = await _context.Work.SingleAsync(w => !w.IsDeleted && w.AnalyseToken == null && w.CurrentDate == OutsideSourceDay);
        realBoundary.ClientId.ShouldBe(AgentB);
        var boundaryExpense = await _context.Expenses.SingleAsync(e => e.WorkId == realBoundary.Id && !e.IsDeleted);
        boundaryExpense.Amount.ShouldBe(BoundaryAmount);
        boundaryExpense.AnalyseToken.ShouldBeNull();
    }

    private async Task<Guid> ApplyAsync()
    {
        var jobId = Guid.NewGuid();
        _cache.Store(jobId, Bitmap(), Bitmap(), sourceAnalyseToken: _sourceToken);

        var (scenario, _, _) = await BuildSut().ApplyAsScenarioAsync(
            jobId, null, CancellationToken.None, evaluateCompliance: false, captureRun: false);
        return scenario.Token;
    }

    private HarmonizerApplyService BuildSut()
    {
        _mediator = Substitute.For<IMediator>();
        _mediator.Send(Arg.Do<BulkAddWorksCommand>(c => _bulkCommands.Add(c)), Arg.Any<CancellationToken>())
            .Returns(new BulkWorksResponse { CreatedIds = [] });

        var unitOfWork = Substitute.For<IUnitOfWork>();
        unitOfWork.CompleteAsync().Returns(_ => _context.SaveChangesAsync());
        unitOfWork
            .ExecuteInTransactionAsync(Arg.Any<Func<Task<(IReadOnlyList<Guid> Ids, Guid Token, AnalyseScenario Scenario)>>>())
            .Returns(ci => ci.Arg<Func<Task<(IReadOnlyList<Guid> Ids, Guid Token, AnalyseScenario Scenario)>>>()());

        var scenarioRepository = Substitute.For<IAnalyseScenarioRepository>();
        scenarioRepository.GetByTokenAsync(_sourceToken, Arg.Any<CancellationToken>())
            .Returns(ci => _context.Set<AnalyseScenario>().AsNoTracking().Single(s => s.Token == _sourceToken));
        scenarioRepository.Add(Arg.Any<AnalyseScenario>())
            .Returns(ci => _context.Set<AnalyseScenario>().AddAsync(ci.Arg<AnalyseScenario>()).AsTask());

        var nameGenerator = Substitute.For<IScenarioNameGenerator>();
        nameGenerator
            .GenerateAsync(
                Arg.Any<ScenarioNameKind>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(),
                Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns("child");

        return new HarmonizerApplyService(
            _cache, _mediator, scenarioRepository, _scenarioService, unitOfWork, _context,
            Substitute.For<IWizardRunCaptureRepository>(), Substitute.For<IScenarioComplianceService>(),
            Substitute.For<IScheduleTimelineService>(), Substitute.For<IScheduleSnapshotMarkerService>(),
            new FixedCompanyClock(new DateTimeOffset(2026, 3, 10, 0, 0, 0, TimeSpan.Zero)),
            nameGenerator, Klacks.UnitTest.TestHelpers.TestScenarioTokenAccess.AllVisible(), NullLogger<HarmonizerApplyService>.Instance);
    }

    /// <summary>
    /// Bitmap as the harmonizer sees the source scenario: both of its works (in range and boundary) moved from agent A
    /// to agent B.
    /// </summary>
    private HarmonyBitmap Bitmap()
    {
        var days = new[] { Day, OutsideSourceDay };
        var cells = new Cell[2, days.Length];
        for (var d = 0; d < days.Length; d++)
        {
            cells[0, d] = Cell.Free();
        }

        cells[1, 0] = WorkCell(_sourceInRangeWork);
        cells[1, 1] = WorkCell(_sourceBoundaryWork);

        var agents = new[]
        {
            new BitmapAgent(AgentA.ToString(), "A", 8m, new HashSet<CellSymbol>()),
            new BitmapAgent(AgentB.ToString(), "B", 8m, new HashSet<CellSymbol>()),
        };
        return new HarmonyBitmap(agents, days, cells);
    }

    private static Cell WorkCell(Work work)
        => new(CellSymbol.Early, work.ShiftId, [work.Id], false,
            work.CurrentDate.ToDateTime(work.StartTime), work.CurrentDate.ToDateTime(work.EndTime), work.WorkTime);

    private Work AddRealWork(DateOnly date, Guid? parentWorkId = null)
    {
        var work = new Work
        {
            Id = Guid.NewGuid(),
            ClientId = AgentA,
            ShiftId = _realShiftId,
            CurrentDate = date,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(16, 0),
            WorkTime = 8m,
            LockLevel = WorkLockLevel.None,
            ParentWorkId = parentWorkId,
        };
        _context.Work.Add(work);
        return work;
    }

    private Task<List<Work>> LiveWorksOf(Guid token)
        => _context.Work.IgnoreQueryFilters().Where(w => w.AnalyseToken == token && !w.IsDeleted).ToListAsync();
}
