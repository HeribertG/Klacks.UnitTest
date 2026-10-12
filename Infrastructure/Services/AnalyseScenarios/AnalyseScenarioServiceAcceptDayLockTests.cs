// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests that accepting a scenario leaves locked days untouched: real works, breaks and notes on a sealed day survive
/// while the scenario's versions are discarded (and counted when they differ), unlocked days are accepted normally,
/// a work slot moved across a locked day is not half applied, locked breaks are not duplicated and pauses follow
/// their work. Also covers the groupless accept scope, which includes the clients of the scenario's notes. Uses global seals (the group-seal attribution needs Npgsql and is covered by the integration test).
/// </summary>

using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.AnalyseScenarios;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Klacks.UnitTest.Infrastructure.Services.AnalyseScenarios;

[TestFixture]
public class AnalyseScenarioServiceAcceptDayLockTests
{
    private static readonly DateOnly From = new(2026, 3, 1);
    private static readonly DateOnly Until = new(2026, 3, 31);
    private static readonly DateOnly SealedDay = new(2026, 3, 10);
    private static readonly DateOnly OpenDay = new(2026, 3, 12);
    private static readonly TimeOnly Start = new(8, 0);
    private static readonly TimeOnly End = new(16, 0);
    private static readonly TimeOnly ChangedStart = new(9, 0);
    private static readonly Guid AbsenceId = Guid.NewGuid();

    private DataBaseContext _context = null!;
    private AnalyseScenarioService _service = null!;
    private Guid _shiftId;
    private Guid _clientId;
    private Guid _token;

    [SetUp]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _service = new AnalyseScenarioService(_context);
        _shiftId = Guid.NewGuid();
        _clientId = Guid.NewGuid();
        _token = Guid.NewGuid();
        _context.Shift.Add(new Shift { Id = _shiftId, Name = "Early" });
        _context.SealedDay.Add(new Klacks.Api.Domain.Models.Schedules.SealedDay { Id = Guid.NewGuid(), Date = SealedDay, GroupId = null });
        _context.SaveChanges();
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public async Task Accept_KeepsRealWorkBreakAndNoteOnSealedDay_AndAcceptsTheOpenDay()
    {
        var sealedWork = AddRealWork(SealedDay);
        var openWork = AddRealWork(OpenDay);
        var sealedBreak = AddRealBreak(SealedDay);
        var sealedNote = AddRealNote(SealedDay, "original");
        await _context.SaveChangesAsync();
        await CloneAsync();

        foreach (var clone in await _context.Work.Where(w => w.AnalyseToken == _token).ToListAsync())
        {
            clone.StartTime = ChangedStart;
        }
        (await _context.Break.SingleAsync(b => b.AnalyseToken == _token && b.CurrentDate == SealedDay)).EndTime = ChangedStart;
        (await _context.ScheduleNotes.SingleAsync(n => n.AnalyseToken == _token && n.CurrentDate == SealedDay)).Content = "changed";
        await _context.SaveChangesAsync();

        var skips = await AcceptAsync();

        var liveOnSealedDay = await LiveRealWorks(SealedDay);
        liveOnSealedDay.ShouldHaveSingleItem().Id.ShouldBe(sealedWork.Id);
        liveOnSealedDay[0].StartTime.ShouldBe(Start);
        var liveBreak = (await _context.Break.Where(b => !b.IsDeleted && b.AnalyseToken == null && b.CurrentDate == SealedDay).ToListAsync())
            .ShouldHaveSingleItem();
        liveBreak.Id.ShouldBe(sealedBreak.Id);
        liveBreak.EndTime.ShouldBe(End);
        var liveNote = (await _context.ScheduleNotes.Where(n => !n.IsDeleted && n.AnalyseToken == null && n.CurrentDate == SealedDay).ToListAsync())
            .ShouldHaveSingleItem();
        liveNote.Id.ShouldBe(sealedNote.Id);
        liveNote.Content.ShouldBe("original");

        var liveOnOpenDay = await LiveRealWorks(OpenDay);
        liveOnOpenDay.ShouldHaveSingleItem().Id.ShouldNotBe(openWork.Id, "the open day is replaced by the scenario");
        liveOnOpenDay[0].StartTime.ShouldBe(ChangedStart);

        skips.ShouldBe(new ScenarioAcceptLockSkips(Works: 1, WorkChanges: 0, Expenses: 0, Breaks: 1, Notes: 1));
    }

    [Test]
    public async Task Accept_EditedExpenseAndAddedWorkChangeOnASealedDay_AreCounted_AndTheRealOnesStay()
    {
        var sealedWork = AddRealWork(SealedDay);
        var realExpense = new Expenses { Id = Guid.NewGuid(), WorkId = sealedWork.Id, Amount = 12m, Description = "Train" };
        _context.Expenses.Add(realExpense);
        await _context.SaveChangesAsync();
        await CloneAsync();

        var clone = await _context.Work.SingleAsync(w => w.AnalyseToken == _token && w.CurrentDate == SealedDay);
        (await _context.Expenses.SingleAsync(e => e.WorkId == clone.Id)).Amount = 30m;
        _context.WorkChange.Add(new WorkChange
        {
            Id = Guid.NewGuid(), WorkId = clone.Id, Type = WorkChangeType.CorrectionEnd, ChangeTime = 1m, AnalyseToken = _token
        });
        await _context.SaveChangesAsync();

        var skips = await AcceptAsync();

        skips.ShouldBe(new ScenarioAcceptLockSkips(Works: 0, WorkChanges: 1, Expenses: 1, Breaks: 0, Notes: 0));
        (await _context.Expenses.Where(e => !e.IsDeleted && e.AnalyseToken == null).ToListAsync())
            .ShouldHaveSingleItem().Amount.ShouldBe(12m);
        (await _context.WorkChange.CountAsync(wc => !wc.IsDeleted && wc.AnalyseToken == null)).ShouldBe(0);
    }

    [Test]
    public async Task Accept_UnchangedChildrenOnASealedDay_AreNotCounted()
    {
        var sealedWork = AddRealWork(SealedDay);
        _context.Expenses.Add(new Expenses { Id = Guid.NewGuid(), WorkId = sealedWork.Id, Amount = 12m, Description = "Train" });
        _context.WorkChange.Add(new WorkChange { Id = Guid.NewGuid(), WorkId = sealedWork.Id, Type = WorkChangeType.CorrectionEnd, ChangeTime = 1m });
        await _context.SaveChangesAsync();
        await CloneAsync();

        var skips = await AcceptAsync();

        skips.Total.ShouldBe(0);
    }

    [Test]
    public async Task Accept_DoesNotPromoteAScenarioWorkAddedOnASealedDay()
    {
        AddRealWork(OpenDay);
        await _context.SaveChangesAsync();
        await CloneAsync();
        var cloneShiftId = (await _context.Shift.SingleAsync(s => s.AnalyseToken == _token)).Id;
        var otherClient = Guid.NewGuid();
        _context.Work.Add(NewWork(otherClient, SealedDay, cloneShiftId, _token));
        await _context.SaveChangesAsync();

        var skips = await AcceptAsync();

        (await LiveRealWorks(SealedDay)).ShouldBeEmpty();
        (await LiveRealWorks(OpenDay)).ShouldHaveSingleItem();
        skips.Works.ShouldBe(1);
    }

    [Test]
    public async Task Accept_UnchangedCopiesOnASealedDay_AreNotCounted()
    {
        var sealedWork = AddRealWork(SealedDay);
        AddRealNote(SealedDay, "same");
        await _context.SaveChangesAsync();
        await CloneAsync();

        var skips = await AcceptAsync();

        (await LiveRealWorks(SealedDay)).ShouldHaveSingleItem().Id.ShouldBe(sealedWork.Id);
        skips.Total.ShouldBe(0);
    }

    [Test]
    public async Task Accept_SlotMovedOntoASealedDay_KeepsTheOriginalAndDiscardsTheClone()
    {
        var openWork = AddRealWork(OpenDay);
        await _context.SaveChangesAsync();
        await CloneAsync();
        (await _context.Work.SingleAsync(w => w.AnalyseToken == _token && w.CurrentDate == OpenDay)).CurrentDate = SealedDay;
        await _context.SaveChangesAsync();

        var skips = await AcceptAsync();

        (await LiveRealWorks(OpenDay)).ShouldHaveSingleItem().Id.ShouldBe(openWork.Id, "half a move would lose the slot");
        (await LiveRealWorks(SealedDay)).ShouldBeEmpty();
        skips.Works.ShouldBe(1);
    }

    [Test]
    public async Task Accept_SlotMovedOffASealedDay_KeepsTheOriginalAndDiscardsTheClone()
    {
        var sealedWork = AddRealWork(SealedDay);
        await _context.SaveChangesAsync();
        await CloneAsync();
        (await _context.Work.SingleAsync(w => w.AnalyseToken == _token && w.CurrentDate == SealedDay)).CurrentDate = OpenDay;
        await _context.SaveChangesAsync();

        var skips = await AcceptAsync();

        (await LiveRealWorks(SealedDay)).ShouldHaveSingleItem().Id.ShouldBe(sealedWork.Id);
        (await LiveRealWorks(OpenDay)).ShouldBeEmpty("promoting the moved clone would duplicate the slot");
        skips.Works.ShouldBe(1);
    }

    [Test]
    public async Task Accept_KeepsALockedBreak_WithoutDuplicatingIt()
    {
        var lockedBreak = AddRealBreak(OpenDay, WorkLockLevel.Confirmed);
        AddRealWork(OpenDay);
        await _context.SaveChangesAsync();
        await CloneAsync();

        var skips = await AcceptAsync();

        var live = await _context.Break.Where(b => !b.IsDeleted && b.AnalyseToken == null && b.CurrentDate == OpenDay).ToListAsync();
        live.ShouldHaveSingleItem().Id.ShouldBe(lockedBreak.Id);
        skips.Total.ShouldBe(0);
    }

    [Test]
    public async Task Accept_PauseOfALockedWork_FollowsTheWork()
    {
        var lockedWork = AddRealWork(OpenDay, WorkLockLevel.Confirmed);
        var pause = AddRealBreak(OpenDay.AddDays(1));
        pause.ParentWorkId = lockedWork.Id;
        await _context.SaveChangesAsync();
        await CloneAsync();

        await AcceptAsync();

        var livePauses = await _context.Break.Where(b => !b.IsDeleted && b.AnalyseToken == null && b.ParentWorkId != null).ToListAsync();
        livePauses.ShouldHaveSingleItem().Id.ShouldBe(pause.Id);
        livePauses[0].ParentWorkId.ShouldBe(lockedWork.Id);
    }

    [Test]
    public async Task Accept_PauseOnASealedDay_KeepsItsWorkAsWell()
    {
        var work = AddRealWork(SealedDay.AddDays(-1));
        var pause = AddRealBreak(SealedDay);
        pause.ParentWorkId = work.Id;
        await _context.SaveChangesAsync();
        await CloneAsync();

        await AcceptAsync();

        (await LiveRealWorks(SealedDay.AddDays(-1))).ShouldHaveSingleItem().Id.ShouldBe(work.Id);
        var livePauses = await _context.Break.Where(b => !b.IsDeleted && b.AnalyseToken == null && b.ParentWorkId != null).ToListAsync();
        livePauses.ShouldHaveSingleItem().Id.ShouldBe(pause.Id);
    }

    [Test]
    public async Task Accept_GrouplessScenario_ReplacesTheNoteOfAClientWithoutWorks_InsteadOfDuplicatingIt()
    {
        AddRealWork(OpenDay);
        var noteOnlyClient = Guid.NewGuid();
        _context.ScheduleNotes.Add(new ScheduleNote { Id = Guid.NewGuid(), ClientId = noteOnlyClient, CurrentDate = OpenDay, Content = "note" });
        await _context.SaveChangesAsync();
        await CloneAsync();

        await AcceptAsync();

        (await _context.ScheduleNotes.CountAsync(n => !n.IsDeleted && n.AnalyseToken == null && n.ClientId == noteOnlyClient))
            .ShouldBe(1);
    }

    [Test]
    public async Task Accept_ReplacementShownAfterMidnightOnASealedDay_KeepsTheRealWorkAndDiscardsTheClone()
    {
        var nightShiftId = Guid.NewGuid();
        _context.Shift.Add(new Shift { Id = nightShiftId, Name = "Night", StartShift = new TimeOnly(22, 0), EndShift = new TimeOnly(6, 0) });
        var nightWork = NewWork(_clientId, SealedDay.AddDays(-1), nightShiftId, null);
        nightWork.StartTime = new TimeOnly(22, 0);
        nightWork.EndTime = new TimeOnly(6, 0);
        _context.Work.Add(nightWork);
        _context.WorkChange.Add(new WorkChange
        {
            Id = Guid.NewGuid(), WorkId = nightWork.Id, Type = WorkChangeType.ReplacementEnd, ChangeTime = 2m,
            ReplaceClientId = Guid.NewGuid()
        });
        await _context.SaveChangesAsync();
        await CloneAsync();
        (await _context.Work.SingleAsync(w => w.AnalyseToken == _token)).Information = "changed";
        await _context.SaveChangesAsync();

        var skips = await AcceptAsync();

        (await LiveRealWorks(SealedDay.AddDays(-1))).ShouldHaveSingleItem().Id.ShouldBe(
            nightWork.Id, "the replacement's 04:00-06:00 part is shown on the sealed following day");
        skips.Works.ShouldBe(1);
    }

    [Test]
    public async Task SlotReplacement_DoesNotDisplaceAScenarioWorkOnASealedDay()
    {
        AddRealWork(SealedDay);
        AddRealWork(OpenDay);
        await _context.SaveChangesAsync();
        await CloneAsync();
        var cloneShiftId = (await _context.Shift.SingleAsync(s => s.AnalyseToken == _token)).Id;

        var displaced = await _service.SoftDeleteClonedWorksOnSlotsAsync(
            _token, From, Until, new HashSet<(Guid, DateOnly)> { (cloneShiftId, SealedDay), (cloneShiftId, OpenDay) },
            CancellationToken.None);
        await _context.SaveChangesAsync();

        displaced.Count.ShouldBe(1, "only the open-day work may be displaced");
        (await _context.Work.CountAsync(w => w.AnalyseToken == _token && !w.IsDeleted && w.CurrentDate == SealedDay))
            .ShouldBe(1);
    }

    private async Task CloneAsync()
    {
        await _service.CloneScenarioDataWithMapsAsync(null, From, Until, _token, null, null, CancellationToken.None);
        await _context.SaveChangesAsync();
    }

    private async Task<ScenarioAcceptLockSkips> AcceptAsync()
    {
        await _service.SoftDeleteRealScheduleDataAsync(null, _token, From, Until, CancellationToken.None);
        var skips = await _service.PromoteScenarioWorksAsync(_token, From, Until, CancellationToken.None);
        await _context.SaveChangesAsync();
        return skips;
    }

    private Task<List<Work>> LiveRealWorks(DateOnly date)
        => _context.Work.Where(w => !w.IsDeleted && w.AnalyseToken == null && w.CurrentDate == date).ToListAsync();

    private Work AddRealWork(DateOnly date, WorkLockLevel lockLevel = WorkLockLevel.None)
    {
        var work = NewWork(_clientId, date, _shiftId, null);
        work.LockLevel = lockLevel;
        _context.Work.Add(work);
        return work;
    }

    private static Work NewWork(Guid clientId, DateOnly date, Guid shiftId, Guid? token) => new()
    {
        Id = Guid.NewGuid(),
        ClientId = clientId,
        ShiftId = shiftId,
        CurrentDate = date,
        StartTime = Start,
        EndTime = End,
        WorkTime = 8m,
        AnalyseToken = token
    };

    private Break AddRealBreak(DateOnly date, WorkLockLevel lockLevel = WorkLockLevel.None)
    {
        var brk = new Break
        {
            Id = Guid.NewGuid(),
            ClientId = _clientId,
            AbsenceId = AbsenceId,
            CurrentDate = date,
            StartTime = Start,
            EndTime = End,
            WorkTime = 8m,
            LockLevel = lockLevel
        };
        _context.Break.Add(brk);
        return brk;
    }

    private ScheduleNote AddRealNote(DateOnly date, string content)
    {
        var note = new ScheduleNote { Id = Guid.NewGuid(), ClientId = _clientId, CurrentDate = date, Content = content };
        _context.ScheduleNotes.Add(note);
        return note;
    }
}
