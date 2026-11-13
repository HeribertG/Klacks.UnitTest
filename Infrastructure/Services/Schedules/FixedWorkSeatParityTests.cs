// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The rule "fixed work" lives in three formulations: Wizard 1 lifts an agent's fixed work into a locked token
/// (WizardHardConstraintBuilder.BuildLockedWorksAsync), takes the seat of a fixed work outside the run out of the demand
/// (LoadForeignFixedSeatsAsync), and the apply keeps a fixed work on a planned slot (ScenarioSlotReplacer). They are not
/// unified on purpose; this parity test compares their results on one fixture, one work per day so a (shift, date) names
/// the work: every top-level work the apply keeps on a day that is not sealed holds a seat (locked token or foreign seat),
/// and every work that holds a seat is kept. The known exception, an unlocked work on a sealed day, is pinned separately.
/// </summary>

using Klacks.Api.Infrastructure.Services.AnalyseScenarios;
using Klacks.Api.Infrastructure.Services.Schedules;
using Klacks.UnitTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules;

[TestFixture]
public class FixedWorkSeatParityTests
{
    private static readonly DateOnly From = new(2026, 3, 2);
    private static readonly DateOnly Until = new(2026, 3, 13);

    private DataBaseContext _context = null!;
    private WizardHardConstraintBuilder _builder = null!;
    private readonly Guid _token = Guid.NewGuid();
    private readonly Guid _shift = Guid.NewGuid();
    private readonly Guid _agent = Guid.NewGuid();
    private readonly Guid _outsider = Guid.NewGuid();
    private readonly Guid _absent = Guid.NewGuid();

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        var keywordProvider = Substitute.For<IScheduleCommandKeywordProvider>();
        keywordProvider.GetAsync(Arg.Any<CancellationToken>()).Returns(ScheduleCommandKeywordTestFactory.Default);
        _builder = new WizardHardConstraintBuilder(_context, keywordProvider);
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public async Task WorksTheApplyKeeps_AreExactlyTheWorksThatHoldASeat_AgentsAndOthersAlike()
    {
        AddWork(_agent, From, WorkLockLevel.Confirmed);
        AddWork(_agent, From.AddDays(1), WorkLockLevel.None, movedFrom: _absent);
        AddCorrection(AddWork(_agent, From.AddDays(2), WorkLockLevel.None));
        AddReplacement(AddWork(_agent, From.AddDays(3), WorkLockLevel.None), substitute: _outsider);
        AddWork(_outsider, From.AddDays(4), WorkLockLevel.Approved);
        AddWork(_outsider, From.AddDays(5), WorkLockLevel.None, movedFrom: _absent);
        AddCorrection(AddWork(_outsider, From.AddDays(6), WorkLockLevel.None));
        AddReplacement(AddWork(_outsider, From.AddDays(7), WorkLockLevel.None), substitute: _agent);
        AddWork(_agent, From.AddDays(8), WorkLockLevel.None);
        AddWork(_outsider, From.AddDays(9), WorkLockLevel.None);
        await _context.SaveChangesAsync();

        var seats = await HeldSeatDaysAsync();
        var kept = await KeptDaysAfterApplyAsync();

        kept.ShouldBe(Enumerable.Range(0, 8).Select(From.AddDays), ignoreOrder: true);
        seats.ShouldBe(kept, ignoreOrder: true);
    }

    [Test]
    public async Task KnownGap_UnlockedWorkOnASealedDay_IsKeptByTheApply_ButHoldsNoSeat_AgentsAndOthersAlike()
    {
        var sealedDay = From.AddDays(2);
        _context.SealedDay.Add(new SealedDay { Id = Guid.NewGuid(), Date = sealedDay, GroupId = null, Level = WorkLockLevel.Closed });
        AddWork(_agent, sealedDay, WorkLockLevel.None);
        AddWork(_outsider, sealedDay, WorkLockLevel.None);
        await _context.SaveChangesAsync();

        var seats = await HeldSeatDaysAsync();
        var keptWorks = await KeptWorksAfterApplyAsync();

        keptWorks.Count(w => w.CurrentDate == sealedDay).ShouldBe(2);
        seats.ShouldNotContain(sealedDay);
    }

    private async Task<List<DateOnly>> HeldSeatDaysAsync()
    {
        var constraints = await _builder.BuildAsync([_agent], From, Until, _token, CancellationToken.None);
        var foreignSeats = await _builder.LoadForeignFixedSeatsAsync([_agent], [_shift], From, Until, _token, CancellationToken.None);

        return constraints.LockedWorks
            .Where(w => w.ShiftRefId == _shift)
            .Select(w => w.Date)
            .Concat(foreignSeats.Select(s => s.Date))
            .ToList();
    }

    private async Task<List<DateOnly>> KeptDaysAfterApplyAsync()
        => (await KeptWorksAfterApplyAsync()).Select(w => w.CurrentDate).ToList();

    private async Task<List<Work>> KeptWorksAfterApplyAsync()
    {
        var everySlot = Enumerable.Range(0, Until.DayNumber - From.DayNumber + 1)
            .Select(offset => (_shift, From.AddDays(offset)))
            .ToHashSet();
        await new ScenarioSlotReplacer(_context).SoftDeleteClonedWorksOnSlotsAsync(_token, From, Until, everySlot, CancellationToken.None);
        await _context.SaveChangesAsync();

        return await _context.Work.IgnoreQueryFilters().AsNoTracking()
            .Where(w => w.AnalyseToken == _token && !w.IsDeleted && w.ParentWorkId == null)
            .ToListAsync();
    }

    private Work AddWork(Guid client, DateOnly date, WorkLockLevel lockLevel, Guid? movedFrom = null)
    {
        var work = new Work
        {
            Id = Guid.NewGuid(),
            ClientId = client,
            ShiftId = _shift,
            CurrentDate = date,
            StartTime = new TimeOnly(6, 0),
            EndTime = new TimeOnly(14, 0),
            WorkTime = 8m,
            LockLevel = lockLevel,
            AnalyseToken = _token,
            MovedFromClientId = movedFrom,
        };
        _context.Work.Add(work);
        return work;
    }

    private void AddCorrection(Work work)
        => _context.WorkChange.Add(new WorkChange
        {
            Id = Guid.NewGuid(),
            WorkId = work.Id,
            Type = WorkChangeType.CorrectionEnd,
            StartTime = new TimeOnly(14, 0),
            EndTime = new TimeOnly(15, 0),
            ChangeTime = 1m,
            AnalyseToken = _token,
        });

    private void AddReplacement(Work work, Guid substitute)
        => _context.WorkChange.Add(new WorkChange
        {
            Id = Guid.NewGuid(),
            WorkId = work.Id,
            Type = WorkChangeType.ReplacementWithin,
            ReplaceClientId = substitute,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(10, 0),
            ChangeTime = 2m,
            AnalyseToken = _token,
        });
}
