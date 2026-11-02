// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// An accept removes the uncovered works of the absences the scenario added, with what hangs on them; everything a planner
/// or a cover settled stays.
/// </summary>

using Klacks.Api.Infrastructure.Services.AnalyseScenarios;
using Klacks.UnitTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Services.AnalyseScenarios;

[TestFixture]
public class ScenarioOpenSlotPromoteTests
{
    private static readonly DateOnly Day = new(2026, 11, 10);
    private static readonly DateOnly From = new(2026, 11, 1);
    private static readonly DateOnly Until = new(2026, 11, 30);
    private static readonly TimeOnly DayStart = new(0, 0);
    private static readonly TimeOnly DayEnd = new(23, 59);

    private DataBaseContext _context = null!;
    private AnalyseScenarioService _service = null!;
    private Guid _token;
    private Guid _elsa;
    private Guid _sick;
    private Guid _onCall;
    private Work _work = null!;

    [SetUp]
    public async Task SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _service = AnalyseScenarioTestServiceFactory.Create(_context);
        _token = Guid.NewGuid();
        _elsa = Guid.NewGuid();
        _sick = Guid.NewGuid();
        _onCall = Guid.NewGuid();
        _context.Absence.AddRange(new Absence { Id = _sick }, new Absence { Id = _onCall, IsOnCall = true });
        _context.AnalyseScenarios.Add(NewScenario(Guid.NewGuid()));
        _work = NewWork(null);
        _context.Work.Add(_work);
        await _context.SaveChangesAsync();
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public async Task ANewAbsence_RemovesTheUncoveredWork_WithEverythingOnIt()
    {
        var subWork = NewWork(_work.Id);
        var subBreak = NewBreak(_sick, _token, _work.Id);
        var change = new WorkChange { Id = Guid.NewGuid(), WorkId = _work.Id, Type = WorkChangeType.CorrectionEnd, AnalyseToken = _token };
        var expense = new Expenses { Id = Guid.NewGuid(), WorkId = _work.Id, AnalyseToken = _token };
        _context.Work.Add(subWork);
        _context.Break.AddRange(NewBreak(_sick, _token), subBreak);
        _context.WorkChange.Add(change);
        _context.Expenses.Add(expense);
        await _context.SaveChangesAsync();

        await PromoteAsync();

        (await IsDeleted(_work)).ShouldBeTrue();
        (await IsDeleted(subWork)).ShouldBeTrue();
        (await _context.Break.IgnoreQueryFilters().SingleAsync(b => b.Id == subBreak.Id)).IsDeleted.ShouldBeTrue();
        (await _context.WorkChange.IgnoreQueryFilters().SingleAsync(c => c.Id == change.Id)).IsDeleted.ShouldBeTrue();
        (await _context.Expenses.IgnoreQueryFilters().SingleAsync(e => e.Id == expense.Id)).IsDeleted.ShouldBeTrue();
        (await _context.Break.CountAsync(b => b.ClientId == _elsa && b.AnalyseToken == null && b.ParentWorkId == null)).ShouldBe(1);
    }

    [Test]
    public async Task AnAbsenceTheRealPlanAlreadyHad_LeavesTheWork()
    {
        _context.Break.AddRange(NewBreak(_sick, _token), NewBreak(_sick, null));
        await _context.SaveChangesAsync();

        await PromoteAsync();

        (await IsDeleted(_work)).ShouldBeFalse();
    }

    [Test]
    public async Task AnOnCallAbsence_LeavesTheWork()
    {
        _context.Break.Add(NewBreak(_onCall, _token));
        await _context.SaveChangesAsync();

        await PromoteAsync();

        (await IsDeleted(_work)).ShouldBeFalse();
    }

    [Test]
    public async Task AReplacedWork_Stays()
    {
        _context.Break.Add(NewBreak(_sick, _token));
        _context.WorkChange.Add(new WorkChange
        {
            Id = Guid.NewGuid(),
            WorkId = _work.Id,
            Type = WorkChangeType.ReplacementWithin,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(16, 0),
            ChangeTime = 8m,
            ReplaceClientId = Guid.NewGuid(),
            AnalyseToken = _token
        });
        await _context.SaveChangesAsync();

        await PromoteAsync();

        (await IsDeleted(_work)).ShouldBeFalse();
    }

    [Test]
    public async Task ADurationWithoutTimes_CountsAsTheWholeDay()
    {
        var duration = NewBreak(_sick, _token);
        duration.EndTime = DayStart;
        _context.Break.Add(duration);
        await _context.SaveChangesAsync();

        await PromoteAsync();

        (await IsDeleted(_work)).ShouldBeTrue();
    }

    [Test]
    public async Task AKey_RemovesNothing()
    {
        var scenario = await _context.AnalyseScenarios.IgnoreQueryFilters().SingleAsync();
        scenario.Kind = AnalyseScenarioKind.Variant;
        _context.Break.Add(NewBreak(_sick, _token));
        await _context.SaveChangesAsync();

        await PromoteAsync();

        (await IsDeleted(_work)).ShouldBeFalse();
    }

    [Test]
    public async Task AScenarioWithoutAGroup_RemovesNothing()
    {
        var scenario = await _context.AnalyseScenarios.SingleAsync();
        scenario.GroupId = null;
        _context.Break.Add(NewBreak(_sick, _token));
        await _context.SaveChangesAsync();

        await PromoteAsync();

        (await IsDeleted(_work)).ShouldBeFalse();
    }

    private async Task PromoteAsync()
    {
        await _service.PromoteScenarioWorksAsync(_token, From, Until, CancellationToken.None);
        await _context.SaveChangesAsync();
    }

    private async Task<bool> IsDeleted(Work work)
        => (await _context.Work.IgnoreQueryFilters().AsNoTracking().SingleAsync(w => w.Id == work.Id)).IsDeleted;

    private AnalyseScenario NewScenario(Guid groupId) => new()
    {
        Id = Guid.NewGuid(),
        Name = "cover",
        GroupId = groupId,
        FromDate = From,
        UntilDate = Until,
        Token = _token,
        RunGroupId = Guid.NewGuid()
    };

    private Work NewWork(Guid? parentId) => new()
    {
        Id = Guid.NewGuid(),
        ClientId = _elsa,
        ShiftId = Guid.NewGuid(),
        ParentWorkId = parentId,
        CurrentDate = Day,
        StartTime = new TimeOnly(8, 0),
        EndTime = new TimeOnly(16, 0),
        AnalyseToken = _token
    };

    private Break NewBreak(Guid absenceId, Guid? token, Guid? parentId = null) => new()
    {
        Id = Guid.NewGuid(),
        ClientId = _elsa,
        AbsenceId = absenceId,
        CurrentDate = Day,
        StartTime = DayStart,
        EndTime = DayEnd,
        WorkTime = 8m,
        ParentWorkId = parentId,
        AnalyseToken = token
    };
}
