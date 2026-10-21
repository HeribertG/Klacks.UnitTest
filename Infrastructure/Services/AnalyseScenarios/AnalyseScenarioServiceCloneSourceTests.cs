// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests for building a scenario on a scenario (CloneScenarioDataWithMapsAsync with a source token): inside the source
/// scenario's range the child copies the source scenario (its breaks and replacements included), outside it the real
/// plan; every clone keeps the real root shift, so the child is accepted straight into the real plan. Also covers the
/// source guards, the cloned temporary memberships, the root shift lookup, the rootless-shift accept guard and that the
/// real-plan clone (no source token) still ignores scenario rows.
/// </summary>

using Klacks.Api.Application.Exceptions;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.AnalyseScenarios;
using Klacks.UnitTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Klacks.UnitTest.Infrastructure.Services.AnalyseScenarios;

[TestFixture]
public class AnalyseScenarioServiceCloneSourceTests
{
    private static readonly DateOnly Day = new(2026, 3, 10);
    private static readonly DateOnly SourceUntil = Day.AddDays(6);
    private static readonly DateOnly InsideSourceDay = Day.AddDays(3);
    private static readonly DateOnly OutsideSourceDay = Day.AddDays(20);
    private static readonly Guid AbsentId = Guid.NewGuid();
    private static readonly Guid ReplacementId = Guid.NewGuid();
    private static readonly Guid BorrowedId = Guid.NewGuid();
    private static readonly Guid AbsenceId = Guid.NewGuid();

    private DataBaseContext _context = null!;
    private AnalyseScenarioService _service = null!;
    private Guid _realShiftId;

    [SetUp]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _service = AnalyseScenarioTestServiceFactory.Create(_context);
        _realShiftId = Guid.NewGuid();
        _context.Shift.Add(new Shift { Id = _realShiftId, Name = "Early" });
        _context.SaveChanges();
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public async Task CloneWithoutSource_CopiesTheRealPlanOnly()
    {
        AddWork(Day, token: null);
        AddWork(Day, token: Guid.NewGuid());
        await _context.SaveChangesAsync();
        var token = Guid.NewGuid();

        await _service.CloneScenarioDataWithMapsAsync(null, Day, Day, token, null, null, CancellationToken.None);
        await _context.SaveChangesAsync();

        (await WorksOf(token)).Count.ShouldBe(1);
        var clone = await _context.Shift.SingleAsync(s => s.AnalyseToken == token);
        clone.ScenarioSourceShiftId.ShouldBe(_realShiftId);
    }

    [Test]
    public async Task CloneFromSource_CopiesTheSourceScenarioInsideItsRange_AndTheRealPlanOutside()
    {
        var sourceToken = await CreateSourceScenarioAsync();
        AddWork(InsideSourceDay, token: null);
        await _context.SaveChangesAsync();
        var childToken = Guid.NewGuid();

        var (shiftIdMap, _) = await _service.CloneScenarioDataWithMapsAsync(
            null, Day, OutsideSourceDay, childToken, null, sourceToken, CancellationToken.None);
        await _context.SaveChangesAsync();

        var childShift = await _context.Shift.SingleAsync(s => s.AnalyseToken == childToken);
        childShift.ScenarioSourceShiftId.ShouldBe(_realShiftId, "a clone of a clone keeps the real root");
        shiftIdMap[_realShiftId].ShouldBe(childShift.Id);

        var childWorks = await WorksOf(childToken);
        childWorks.Select(w => w.CurrentDate).OrderBy(d => d).ShouldBe(new[] { Day, OutsideSourceDay });
        childWorks.ShouldAllBe(w => w.ShiftId == childShift.Id);

        var childBreak = await _context.Break.SingleAsync(b => b.AnalyseToken == childToken);
        childBreak.ClientId.ShouldBe(AbsentId);
        childBreak.CurrentDate.ShouldBe(Day);

        var childChange = await _context.WorkChange.SingleAsync(wc => wc.AnalyseToken == childToken);
        childChange.ReplaceClientId.ShouldBe(ReplacementId);
        childChange.WorkId.ShouldBe(childWorks.Single(w => w.CurrentDate == Day).Id);
    }

    [Test]
    public async Task CloneFromSource_CopiesTheTemporaryMemberships()
    {
        var sourceToken = await CreateSourceScenarioAsync();
        var groupId = Guid.NewGuid();
        await _service.AddScenarioMembershipAsync(sourceToken, BorrowedId, groupId, Day, Day, CancellationToken.None);
        await _context.SaveChangesAsync();
        var childToken = Guid.NewGuid();

        await _service.CloneScenarioDataWithMapsAsync(null, Day, Day, childToken, null, sourceToken, CancellationToken.None);
        await _context.SaveChangesAsync();

        var membership = await _context.Set<GroupItem>().SingleAsync(gi => gi.AnalyseToken == childToken && gi.ShiftId == null);
        membership.ClientId.ShouldBe(BorrowedId);
        membership.GroupId.ShouldBe(groupId);
    }

    [Test]
    public async Task CloneFromGroupedSource_CopiesTheSourceCloneShift_NotItsRealRootAgain()
    {
        var groupId = Guid.NewGuid();
        _context.Set<GroupItem>().Add(new GroupItem { Id = Guid.NewGuid(), GroupId = groupId, ShiftId = _realShiftId });
        AddWork(Day, token: null);
        await _context.SaveChangesAsync();
        var sourceToken = Guid.NewGuid();
        await _service.CloneScenarioDataWithMapsAsync(groupId, Day, SourceUntil, sourceToken, null, null, CancellationToken.None);
        AddScenario(sourceToken, groupId, AnalyseScenarioStatus.Active);
        await _context.SaveChangesAsync();
        var childToken = Guid.NewGuid();

        await _service.CloneScenarioDataWithMapsAsync(groupId, Day, SourceUntil, childToken, null, sourceToken, CancellationToken.None);
        await _context.SaveChangesAsync();

        var childShift = await _context.Shift.SingleAsync(s => s.AnalyseToken == childToken);
        childShift.ScenarioSourceShiftId.ShouldBe(_realShiftId);
        (await WorksOf(childToken)).ShouldHaveSingleItem().ShiftId.ShouldBe(childShift.Id);
    }

    [Test]
    public async Task AcceptingAChildScenario_PromotesOntoTheRealRootShift()
    {
        var sourceToken = await CreateSourceScenarioAsync();
        var childToken = Guid.NewGuid();
        await _service.CloneScenarioDataWithMapsAsync(
            null, Day, SourceUntil, childToken, null, sourceToken, CancellationToken.None);
        await _context.SaveChangesAsync();

        await _service.ValidateNoAcceptConflictsAsync(childToken, CancellationToken.None);
        await _service.SoftDeleteRealScheduleDataAsync(null, childToken, Day, SourceUntil, CancellationToken.None);
        await _service.PromoteScenarioWorksAsync(childToken, Day, SourceUntil, CancellationToken.None);
        await _context.SaveChangesAsync();

        var realWork = await _context.Work.SingleAsync(w => !w.IsDeleted && w.AnalyseToken == null && w.CurrentDate == Day);
        realWork.ShiftId.ShouldBe(_realShiftId);
        (await _context.Work.CountAsync(w => !w.IsDeleted && w.AnalyseToken == null && w.CurrentDate == OutsideSourceDay))
            .ShouldBe(1, "the real work outside the accepted range stays untouched");
        var promotedChange = await _context.WorkChange.SingleAsync(wc => !wc.IsDeleted && wc.WorkId == realWork.Id);
        promotedChange.AnalyseToken.ShouldBeNull();
        promotedChange.ReplaceClientId.ShouldBe(ReplacementId);
        var promotedBreak = await _context.Break.SingleAsync(b => !b.IsDeleted && b.AnalyseToken == null);
        promotedBreak.ClientId.ShouldBe(AbsentId);
    }

    [Test]
    public async Task CloneFromSource_UnknownToken_IsRefused()
    {
        await Should.ThrowAsync<KeyNotFoundException>(() => _service.CloneScenarioDataWithMapsAsync(
            null, Day, Day, Guid.NewGuid(), null, Guid.NewGuid(), CancellationToken.None));
    }

    [Test]
    public async Task CloneFromSource_DecidedScenario_IsRefused()
    {
        var sourceToken = Guid.NewGuid();
        AddScenario(sourceToken, null, AnalyseScenarioStatus.Accepted);
        await _context.SaveChangesAsync();

        await Should.ThrowAsync<ScenarioNotActiveException>(() => _service.CloneScenarioDataWithMapsAsync(
            null, Day, Day, Guid.NewGuid(), null, sourceToken, CancellationToken.None));
    }

    [Test]
    public async Task CloneFromSource_GroupOutsideTheSourceGroup_IsRefused()
    {
        var sourceToken = Guid.NewGuid();
        AddScenario(sourceToken, Guid.NewGuid(), AnalyseScenarioStatus.Active);
        await _context.SaveChangesAsync();

        await Should.ThrowAsync<ConflictException>(() => _service.CloneScenarioDataWithMapsAsync(
            Guid.NewGuid(), Day, Day, Guid.NewGuid(), null, sourceToken, CancellationToken.None));
    }

    [Test]
    public async Task CloneFromGroupedSource_WithoutAGroup_InheritsTheSourceGroup()
    {
        var groupId = Guid.NewGuid();
        var otherShiftId = Guid.NewGuid();
        _context.Shift.Add(new Shift { Id = otherShiftId, Name = "Other group" });
        _context.Set<GroupItem>().Add(new GroupItem { Id = Guid.NewGuid(), GroupId = groupId, ShiftId = _realShiftId });
        await _context.SaveChangesAsync();
        var sourceToken = Guid.NewGuid();
        await _service.CloneScenarioDataWithMapsAsync(groupId, Day, SourceUntil, sourceToken, null, null, CancellationToken.None);
        AddScenario(sourceToken, groupId, AnalyseScenarioStatus.Active);
        await _context.SaveChangesAsync();
        var childToken = Guid.NewGuid();

        await _service.CloneScenarioDataWithMapsAsync(null, Day, SourceUntil, childToken, null, sourceToken, CancellationToken.None);
        await _context.SaveChangesAsync();

        var childShift = await _context.Shift.SingleAsync(s => s.AnalyseToken == childToken);
        childShift.ScenarioSourceShiftId.ShouldBe(_realShiftId, "only the source group's shift, not the whole company");
    }

    [Test]
    public async Task AcceptingAChild_DoesNotDuplicateTheRealBreaksAndNotesOfABorrowedEmployee()
    {
        var groupId = Guid.NewGuid();
        _context.Set<GroupItem>().Add(new GroupItem { Id = Guid.NewGuid(), GroupId = groupId, ShiftId = _realShiftId });
        _context.Set<GroupItem>().Add(new GroupItem { Id = Guid.NewGuid(), GroupId = groupId, ClientId = AbsentId });
        AddWork(Day, token: null);
        await _context.SaveChangesAsync();
        var sourceToken = Guid.NewGuid();
        await _service.CloneScenarioDataWithMapsAsync(groupId, Day, SourceUntil, sourceToken, null, null, CancellationToken.None);
        AddScenario(sourceToken, groupId, AnalyseScenarioStatus.Active);
        await _service.AddScenarioMembershipAsync(sourceToken, BorrowedId, groupId, Day, SourceUntil, CancellationToken.None);
        AddBreak(BorrowedId, OutsideSourceDay, token: null);
        _context.ScheduleNotes.Add(new ScheduleNote { Id = Guid.NewGuid(), ClientId = BorrowedId, CurrentDate = OutsideSourceDay, Content = "home" });
        await _context.SaveChangesAsync();
        var childToken = Guid.NewGuid();

        await _service.CloneScenarioDataWithMapsAsync(groupId, Day, OutsideSourceDay, childToken, null, sourceToken, CancellationToken.None);
        await _context.SaveChangesAsync();
        await _service.ValidateNoAcceptConflictsAsync(childToken, CancellationToken.None);
        await _service.SoftDeleteRealScheduleDataAsync(groupId, childToken, Day, OutsideSourceDay, CancellationToken.None);
        await _service.PromoteScenarioWorksAsync(childToken, Day, OutsideSourceDay, CancellationToken.None);
        await _context.SaveChangesAsync();

        (await _context.Break.CountAsync(b => !b.IsDeleted && b.AnalyseToken == null && b.ClientId == BorrowedId)).ShouldBe(1);
        (await _context.ScheduleNotes.CountAsync(n => !n.IsDeleted && n.AnalyseToken == null && n.ClientId == BorrowedId)).ShouldBe(1);
    }

    [Test]
    public async Task CloneFromSource_SubBreakOfANightWorkAtTheSourceEdge_FollowsItsParentWork()
    {
        var realNightWork = AddWork(SourceUntil, token: null);
        AddBreak(AbsentId, SourceUntil.AddDays(1), token: null, parentWorkId: realNightWork.Id);
        await _context.SaveChangesAsync();
        var sourceToken = Guid.NewGuid();
        await _service.CloneScenarioDataWithMapsAsync(null, Day, SourceUntil, sourceToken, null, null, CancellationToken.None);
        AddScenario(sourceToken, null, AnalyseScenarioStatus.Active);
        await _context.SaveChangesAsync();
        var childToken = Guid.NewGuid();

        await _service.CloneScenarioDataWithMapsAsync(null, Day, OutsideSourceDay, childToken, null, sourceToken, CancellationToken.None);
        await _context.SaveChangesAsync();

        var childNightWork = (await WorksOf(childToken)).Single(w => w.CurrentDate == SourceUntil);
        var childBreaks = await _context.Break.Where(b => b.AnalyseToken == childToken).ToListAsync();
        childBreaks.ShouldHaveSingleItem().ParentWorkId.ShouldBe(childNightWork.Id, "the pause stays with its work, never an orphan absence");
    }

    [Test]
    public async Task AcceptingAChild_StampsTheSourcesOpenReplacementRequestApplied_AndLeavesTheSourceRowAlone()
    {
        var sourceToken = await CreateSourceScenarioAsync();
        var sourceRequestId = Guid.NewGuid();
        _context.ReplacementRequests.Add(new ReplacementRequest
        {
            Id = sourceRequestId,
            AbsentClientId = AbsentId,
            CandidateClientId = ReplacementId,
            ShiftId = _realShiftId,
            Date = Day,
            Outcome = ReplacementRequestOutcome.Proposed,
            AnalyseToken = sourceToken
        });
        await _context.SaveChangesAsync();
        var childToken = Guid.NewGuid();

        await _service.CloneScenarioDataWithMapsAsync(null, Day, SourceUntil, childToken, null, sourceToken, CancellationToken.None);
        await _context.SaveChangesAsync();
        await _service.SoftDeleteRealScheduleDataAsync(null, childToken, Day, SourceUntil, CancellationToken.None);
        await _service.PromoteScenarioWorksAsync(childToken, Day, SourceUntil, CancellationToken.None);
        await _context.SaveChangesAsync();

        var childRequest = await _context.ReplacementRequests.SingleAsync(r => r.AnalyseToken == childToken);
        childRequest.AppliedAtUtc.ShouldNotBeNull();
        childRequest.WorkChangeId.ShouldNotBeNull();
        (await _context.ReplacementRequests.SingleAsync(r => r.Id == sourceRequestId)).AppliedAtUtc.ShouldBeNull();
    }

    [Test]
    public async Task ResolveRootShiftIds_MapsClonesToTheirRoot_AndLeavesRealShiftsOut()
    {
        var cloneId = Guid.NewGuid();
        _context.Shift.Add(new Shift { Id = cloneId, Name = "Clone", AnalyseToken = Guid.NewGuid(), ScenarioSourceShiftId = _realShiftId });
        await _context.SaveChangesAsync();

        var roots = await _service.ResolveRootShiftIdsAsync([cloneId, _realShiftId], CancellationToken.None);

        roots.Count.ShouldBe(1);
        roots[cloneId].ShouldBe(_realShiftId);
    }

    [Test]
    public async Task Accept_WorksOnAScenarioShiftWithoutRoot_IsRefused()
    {
        var token = Guid.NewGuid();
        var rootlessShiftId = Guid.NewGuid();
        _context.Shift.Add(new Shift { Id = rootlessShiftId, Name = "Scenario only", AnalyseToken = token });
        _context.Work.Add(new Work { Id = Guid.NewGuid(), ClientId = AbsentId, ShiftId = rootlessShiftId, CurrentDate = Day, AnalyseToken = token });
        await _context.SaveChangesAsync();

        await Should.ThrowAsync<ConflictException>(() => _service.ValidateNoAcceptConflictsAsync(token, CancellationToken.None));
    }

    /// <summary>
    /// A source scenario over [Day, SourceUntil] cloned from the real plan (one work on Day and one far outside), then
    /// changed the way a cover changes it: an absence break and a replacement on its copy of the Day work.
    /// </summary>
    private async Task<Guid> CreateSourceScenarioAsync()
    {
        AddWork(Day, token: null);
        AddWork(OutsideSourceDay, token: null);
        await _context.SaveChangesAsync();

        var sourceToken = Guid.NewGuid();
        var (_, workIdMap) = await _service.CloneScenarioDataWithMapsAsync(
            null, Day, SourceUntil, sourceToken, null, null, CancellationToken.None);
        AddScenario(sourceToken, null, AnalyseScenarioStatus.Active);
        await _context.SaveChangesAsync();

        var sourceDayWorkId = (await WorksOf(sourceToken)).Single(w => w.CurrentDate == Day).Id;
        _context.Break.Add(new Break
        {
            Id = Guid.NewGuid(),
            ClientId = AbsentId,
            AbsenceId = AbsenceId,
            CurrentDate = Day,
            StartTime = new TimeOnly(0, 0),
            EndTime = new TimeOnly(23, 59),
            AnalyseToken = sourceToken
        });
        _context.WorkChange.Add(new WorkChange
        {
            Id = Guid.NewGuid(),
            WorkId = sourceDayWorkId,
            Type = WorkChangeType.ReplacementWithin,
            ReplaceClientId = ReplacementId,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(16, 0),
            ChangeTime = 8m,
            AnalyseToken = sourceToken
        });
        await _context.SaveChangesAsync();
        workIdMap.Count.ShouldBe(2);
        return sourceToken;
    }

    private Work AddWork(DateOnly date, Guid? token)
    {
        var work = new Work
        {
            Id = Guid.NewGuid(),
            ClientId = AbsentId,
            ShiftId = _realShiftId,
            CurrentDate = date,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(16, 0),
            WorkTime = 8m,
            AnalyseToken = token
        };
        _context.Work.Add(work);
        return work;
    }

    private void AddBreak(Guid clientId, DateOnly date, Guid? token, Guid? parentWorkId = null)
        => _context.Break.Add(new Break
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            AbsenceId = AbsenceId,
            CurrentDate = date,
            StartTime = new TimeOnly(0, 0),
            EndTime = new TimeOnly(23, 59),
            ParentWorkId = parentWorkId,
            AnalyseToken = token
        });

    private void AddScenario(Guid token, Guid? groupId, AnalyseScenarioStatus status)
        => _context.Set<AnalyseScenario>().Add(new AnalyseScenario
        {
            Id = Guid.NewGuid(),
            Name = "Source",
            Token = token,
            GroupId = groupId,
            FromDate = Day,
            UntilDate = SourceUntil,
            Status = status
        });

    private Task<List<Work>> WorksOf(Guid token)
        => _context.Work.Where(w => w.AnalyseToken == token && !w.IsDeleted).ToListAsync();
}
