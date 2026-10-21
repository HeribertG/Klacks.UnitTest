// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Characterization tests of AnalyseScenarioService behaviour that no other test pins directly: the group hierarchy
/// walk, the scenario lookups (break ids, shift ids with roots), the full reject/delete sweep over every scenario row
/// type, the shift-scoped half of the accept promote and the accept conflict checks. Written against the unsplit
/// service so the split into collaborators can be proven behaviour-neutral.
/// </summary>

using Klacks.Api.Application.Exceptions;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Scheduling;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.AnalyseScenarios;
using Klacks.UnitTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Klacks.UnitTest.Infrastructure.Services.AnalyseScenarios;

[TestFixture]
public class AnalyseScenarioServiceCharacterizationTests
{
    private static readonly DateOnly From = new(2026, 3, 1);
    private static readonly DateOnly Until = new(2026, 3, 31);
    private static readonly DateOnly Day = new(2026, 3, 10);
    private static readonly DateOnly BoundaryDay = new(2026, 2, 25);
    private static readonly TimeOnly Start = new(8, 0);
    private static readonly TimeOnly End = new(16, 0);

    private DataBaseContext _context = null!;
    private AnalyseScenarioService _service = null!;
    private Guid _token;

    [SetUp]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _service = AnalyseScenarioTestServiceFactory.Create(_context);
        _token = Guid.NewGuid();
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public async Task GetGroupHierarchyIds_WalksLiveDescendantsBreadthFirst_AndSkipsDeletedAndForeignGroups()
    {
        var root = AddGroup(null);
        var childA = AddGroup(root);
        var childB = AddGroup(root);
        var grandChild = AddGroup(childA);
        var deletedChild = AddGroup(root, isDeleted: true);
        AddGroup(deletedChild);
        AddGroup(null);
        await _context.SaveChangesAsync();

        var ids = await _service.GetGroupHierarchyIdsAsync(root, CancellationToken.None);

        ids.Count.ShouldBe(4);
        ids[0].ShouldBe(root);
        ids.Skip(1).Take(2).ShouldBe(new[] { childA, childB }, ignoreOrder: true);
        ids[3].ShouldBe(grandChild);
    }

    [Test]
    public async Task GetScenarioBreakIds_ReturnsOnlyLiveBreaksOfTheToken()
    {
        var live = AddBreak(_token, Day);
        AddBreak(_token, Day).IsDeleted = true;
        AddBreak(null, Day);
        AddBreak(Guid.NewGuid(), Day);
        await _context.SaveChangesAsync();

        var ids = await _service.GetScenarioBreakIdsAsync(_token, CancellationToken.None);

        ids.ShouldHaveSingleItem().ShouldBe(live.Id);
    }

    [Test]
    public async Task GetScenarioShiftIdsWithRoots_ReturnsLiveCloneIdsAndTheirRoots()
    {
        var rootId = Guid.NewGuid();
        var clone = AddShift(_token, rootId);
        var rootless = AddShift(_token, null);
        AddShift(_token, Guid.NewGuid()).IsDeleted = true;
        AddShift(Guid.NewGuid(), Guid.NewGuid());
        await _context.SaveChangesAsync();

        var ids = await _service.GetScenarioShiftIdsWithRootsAsync(_token, CancellationToken.None);

        ids.ShouldBe(new[] { clone.Id, rootless.Id, rootId }, ignoreOrder: true);
    }

    [Test]
    public async Task SoftDeleteScenarioData_SoftDeletesEveryScenarioRowType_AndLeavesRealAndForeignRows()
    {
        var shift = AddShift(_token, Guid.NewGuid());
        var work = AddWork(_token, shift.Id, Day, null);
        var subWork = AddWork(null, shift.Id, Day, work.Id);
        var workChange = AddWorkChange(work.Id, _token);
        var subWorkChange = AddWorkChange(subWork.Id, null);
        var expense = AddExpense(work.Id, _token);
        var scenarioBreak = AddBreak(_token, Day);
        var subBreak = AddBreak(null, Day);
        subBreak.ParentWorkId = work.Id;
        var requirement = new ShiftRequiredQualification { Id = Guid.NewGuid(), ShiftId = shift.Id, QualificationId = Guid.NewGuid() };
        _context.ShiftRequiredQualification.Add(requirement);
        var note = AddNote(_token, Day);
        var command = AddCommand(_token, Day);
        var softening = new WorkSoftening { Id = Guid.NewGuid(), ClientId = Guid.NewGuid(), CurrentDate = Day, AnalyseToken = _token };
        _context.WorkSoftening.Add(softening);
        var preference = AddPreference(shift.Id, _token);
        var shiftExpense = AddShiftExpense(shift.Id, _token);
        var shiftMembership = AddGroupItem(_token, shift.Id, null);
        var clientMembership = AddGroupItem(_token, null, Guid.NewGuid());
        var proposed = AddRequest(_token, ReplacementRequestOutcome.Proposed);
        var recorded = AddRequest(_token, ReplacementRequestOutcome.Requested);
        var constraint = AddConstraint(_token);

        var realShift = AddShift(null, null);
        var realWork = AddWork(null, realShift.Id, Day, null);
        var realWorkChange = AddWorkChange(realWork.Id, null);
        var realBreak = AddBreak(null, Day);
        var realNote = AddNote(null, Day);
        var foreignWork = AddWork(Guid.NewGuid(), shift.Id, Day, null);
        var foreignConstraint = AddConstraint(Guid.NewGuid());
        await _context.SaveChangesAsync();

        await _service.SoftDeleteScenarioDataAsync(_token, CancellationToken.None);
        await _context.SaveChangesAsync();

        (await Deleted(_context.Work, work.Id)).ShouldBeTrue();
        (await Deleted(_context.Work, subWork.Id)).ShouldBeTrue("a sub-work of a scenario work is swept as an orphan");
        (await Deleted(_context.WorkChange, workChange.Id)).ShouldBeTrue();
        (await Deleted(_context.WorkChange, subWorkChange.Id)).ShouldBeTrue();
        (await Deleted(_context.Expenses, expense.Id)).ShouldBeTrue();
        (await Deleted(_context.Break, scenarioBreak.Id)).ShouldBeTrue();
        (await Deleted(_context.Break, subBreak.Id)).ShouldBeTrue("a pause of a scenario work is swept as an orphan");
        (await Deleted(_context.Shift, shift.Id)).ShouldBeTrue();
        (await Deleted(_context.ShiftRequiredQualification, requirement.Id)).ShouldBeTrue();
        (await Deleted(_context.ScheduleNotes, note.Id)).ShouldBeTrue();
        (await Deleted(_context.ScheduleCommands, command.Id)).ShouldBeTrue();
        (await Deleted(_context.WorkSoftening, softening.Id)).ShouldBeTrue();
        (await Deleted(_context.ClientShiftPreference, preference.Id)).ShouldBeTrue();
        (await Deleted(_context.ShiftExpenses, shiftExpense.Id)).ShouldBeTrue();
        (await Deleted(_context.GroupItem, shiftMembership.Id)).ShouldBeTrue();
        (await Deleted(_context.GroupItem, clientMembership.Id)).ShouldBeTrue();
        (await Deleted(_context.ReplacementRequests, proposed.Id)).ShouldBeTrue();
        (await Deleted(_context.ReplacementRequests, recorded.Id)).ShouldBeFalse("a recorded answer is a fact and stays");
        (await Deleted(_context.PlanningConstraint, constraint.Id)).ShouldBeTrue();

        (await Deleted(_context.Shift, realShift.Id)).ShouldBeFalse();
        (await Deleted(_context.Work, realWork.Id)).ShouldBeFalse();
        (await Deleted(_context.WorkChange, realWorkChange.Id)).ShouldBeFalse();
        (await Deleted(_context.Break, realBreak.Id)).ShouldBeFalse();
        (await Deleted(_context.ScheduleNotes, realNote.Id)).ShouldBeFalse();
        (await Deleted(_context.Work, foreignWork.Id)).ShouldBeFalse();
        (await Deleted(_context.PlanningConstraint, foreignConstraint.Id)).ShouldBeFalse();
    }

    [Test]
    public async Task PromoteScenarioWorks_PromotesPeriodRows_DiscardsBoundaryAndShiftScopedRows()
    {
        var realShift = AddShift(null, null);
        var cloneShift = AddShift(_token, realShift.Id);
        var promoted = AddWork(_token, cloneShift.Id, Day, null);
        promoted.ClonedFromWorkId = Guid.NewGuid();
        var promotedChange = AddWorkChange(promoted.Id, _token);
        var promotedExpense = AddExpense(promoted.Id, _token);
        var boundary = AddWork(_token, cloneShift.Id, BoundaryDay, null);
        var boundaryChange = AddWorkChange(boundary.Id, _token);
        var boundaryExpense = AddExpense(boundary.Id, _token);
        var requirement = new ShiftRequiredQualification { Id = Guid.NewGuid(), ShiftId = cloneShift.Id, QualificationId = Guid.NewGuid() };
        _context.ShiftRequiredQualification.Add(requirement);
        var command = AddCommand(_token, Day);
        var preference = AddPreference(cloneShift.Id, _token);
        var shiftExpense = AddShiftExpense(cloneShift.Id, _token);
        var shiftMembership = AddGroupItem(_token, cloneShift.Id, null);
        var clientMembership = AddGroupItem(_token, null, Guid.NewGuid());
        var constraint = AddConstraint(_token);
        await _context.SaveChangesAsync();

        var skips = await _service.PromoteScenarioWorksAsync(_token, From, Until, CancellationToken.None);
        await _context.SaveChangesAsync();

        skips.Total.ShouldBe(0);
        var promotedRow = await _context.Work.IgnoreQueryFilters().SingleAsync(w => w.Id == promoted.Id);
        promotedRow.IsDeleted.ShouldBeFalse();
        promotedRow.AnalyseToken.ShouldBeNull();
        promotedRow.ClonedFromWorkId.ShouldBeNull();
        promotedRow.ShiftId.ShouldBe(realShift.Id);
        (await _context.WorkChange.IgnoreQueryFilters().SingleAsync(wc => wc.Id == promotedChange.Id)).AnalyseToken.ShouldBeNull();
        (await _context.Expenses.IgnoreQueryFilters().SingleAsync(e => e.Id == promotedExpense.Id)).AnalyseToken.ShouldBeNull();

        (await Deleted(_context.Work, boundary.Id)).ShouldBeTrue();
        (await Deleted(_context.WorkChange, boundaryChange.Id)).ShouldBeTrue();
        (await Deleted(_context.Expenses, boundaryExpense.Id)).ShouldBeTrue();
        (await Deleted(_context.Shift, cloneShift.Id)).ShouldBeTrue();
        (await Deleted(_context.Shift, realShift.Id)).ShouldBeFalse();
        (await Deleted(_context.ShiftRequiredQualification, requirement.Id)).ShouldBeTrue();
        (await Deleted(_context.ScheduleCommands, command.Id)).ShouldBeTrue();
        (await Deleted(_context.ClientShiftPreference, preference.Id)).ShouldBeTrue();
        (await Deleted(_context.ShiftExpenses, shiftExpense.Id)).ShouldBeTrue();
        (await Deleted(_context.GroupItem, shiftMembership.Id)).ShouldBeTrue();
        var membership = await _context.GroupItem.IgnoreQueryFilters().SingleAsync(gi => gi.Id == clientMembership.Id);
        membership.IsDeleted.ShouldBeFalse();
        membership.AnalyseToken.ShouldBeNull();
        (await Deleted(_context.PlanningConstraint, constraint.Id)).ShouldBeTrue();
    }

    [Test]
    public async Task ValidateNoAcceptConflicts_MissingSource_Throws()
    {
        AddShift(_token, Guid.NewGuid());
        await _context.SaveChangesAsync();

        var ex = await Should.ThrowAsync<ConflictException>(() => _service.ValidateNoAcceptConflictsAsync(_token, CancellationToken.None));

        ex.Message.ShouldStartWith("Cannot accept scenario: source shifts no longer exist:");
    }

    [Test]
    public async Task ValidateNoAcceptConflicts_DeletedSource_Throws()
    {
        var source = AddShift(null, null);
        source.IsDeleted = true;
        AddShift(_token, source.Id);
        await _context.SaveChangesAsync();

        var ex = await Should.ThrowAsync<ConflictException>(() => _service.ValidateNoAcceptConflictsAsync(_token, CancellationToken.None));

        ex.Message.ShouldStartWith("Cannot accept scenario: source shifts have been deleted:");
    }

    [Test]
    public async Task ValidateNoAcceptConflicts_SourceSubtreeChanged_Throws()
    {
        var source = AddShift(null, null);
        var clone = AddShift(_token, source.Id);
        clone.SourceChildCountSnapshot = 0;
        AddShift(null, null).ParentId = source.Id;
        await _context.SaveChangesAsync();

        var ex = await Should.ThrowAsync<ConflictException>(() => _service.ValidateNoAcceptConflictsAsync(_token, CancellationToken.None));

        ex.Message.ShouldContain("subtree changed after scenario creation (children snapshot=0, current=1)");
    }

    [Test]
    public async Task ValidateNoAcceptConflicts_RootlessShiftInUse_ThrowsBeforeTheSourceChecks()
    {
        var rootless = AddShift(_token, null);
        AddWork(_token, rootless.Id, Day, null);
        AddShift(_token, Guid.NewGuid());
        await _context.SaveChangesAsync();

        var ex = await Should.ThrowAsync<ConflictException>(() => _service.ValidateNoAcceptConflictsAsync(_token, CancellationToken.None));

        ex.Message.ShouldStartWith("Cannot accept scenario: works are planned on scenario shifts that have no shift in the original plan:");
    }

    [Test]
    public async Task ValidateNoAcceptConflicts_UnchangedSource_DoesNotThrow()
    {
        var source = AddShift(null, null);
        AddShift(null, null).ParentId = source.Id;
        var clone = AddShift(_token, source.Id);
        clone.SourceChildCountSnapshot = 1;
        await _context.SaveChangesAsync();

        await Should.NotThrowAsync(() => _service.ValidateNoAcceptConflictsAsync(_token, CancellationToken.None));
    }

    [Test]
    public async Task SoftDeleteRealScheduleData_WithoutGroup_DeletesOnlyTheScenarioFootprint()
    {
        var realShift = AddShift(null, null);
        var otherShift = AddShift(null, null);
        var cloneShift = AddShift(_token, realShift.Id);
        var clientId = Guid.NewGuid();
        var otherClientId = Guid.NewGuid();
        var footprintWork = AddWork(null, realShift.Id, Day, null);
        var outsideWork = AddWork(null, otherShift.Id, Day, null);
        AddWork(_token, cloneShift.Id, Day, null).ClientId = clientId;
        var footprintBreak = AddBreak(null, Day);
        footprintBreak.ClientId = clientId;
        var outsideBreak = AddBreak(null, Day);
        outsideBreak.ClientId = otherClientId;
        var noteClientId = Guid.NewGuid();
        AddNote(_token, Day).ClientId = noteClientId;
        var footprintNote = AddNote(null, Day);
        footprintNote.ClientId = noteClientId;
        var outsideNote = AddNote(null, Day);
        outsideNote.ClientId = otherClientId;
        await _context.SaveChangesAsync();

        await _service.SoftDeleteRealScheduleDataAsync(null, _token, From, Until, CancellationToken.None);
        await _context.SaveChangesAsync();

        (await Deleted(_context.Work, footprintWork.Id)).ShouldBeTrue();
        (await Deleted(_context.Work, outsideWork.Id)).ShouldBeFalse();
        (await Deleted(_context.Break, footprintBreak.Id)).ShouldBeTrue();
        (await Deleted(_context.Break, outsideBreak.Id)).ShouldBeFalse();
        (await Deleted(_context.ScheduleNotes, footprintNote.Id)).ShouldBeTrue();
        (await Deleted(_context.ScheduleNotes, outsideNote.Id)).ShouldBeFalse();
    }

    private static async Task<bool> Deleted<TEntity>(DbSet<TEntity> set, Guid id)
        where TEntity : Klacks.Api.Domain.Common.BaseEntity
        => (await set.IgnoreQueryFilters().AsNoTracking().SingleAsync(e => e.Id == id)).IsDeleted;

    private Guid AddGroup(Guid? parent, bool isDeleted = false)
    {
        var group = new Group { Id = Guid.NewGuid(), Name = "Group", Parent = parent, IsDeleted = isDeleted };
        _context.Group.Add(group);
        return group.Id;
    }

    private Shift AddShift(Guid? token, Guid? sourceShiftId)
    {
        var shift = new Shift { Id = Guid.NewGuid(), Name = "Early", AnalyseToken = token, ScenarioSourceShiftId = sourceShiftId };
        _context.Shift.Add(shift);
        return shift;
    }

    private Work AddWork(Guid? token, Guid shiftId, DateOnly date, Guid? parentWorkId)
    {
        var work = new Work
        {
            Id = Guid.NewGuid(), AnalyseToken = token, ShiftId = shiftId, ClientId = Guid.NewGuid(), CurrentDate = date,
            StartTime = Start, EndTime = End, WorkTime = 8m, ParentWorkId = parentWorkId
        };
        _context.Work.Add(work);
        return work;
    }

    private WorkChange AddWorkChange(Guid workId, Guid? token)
    {
        var change = new WorkChange { Id = Guid.NewGuid(), WorkId = workId, Type = WorkChangeType.CorrectionEnd, ChangeTime = 1m, AnalyseToken = token };
        _context.WorkChange.Add(change);
        return change;
    }

    private Expenses AddExpense(Guid workId, Guid? token)
    {
        var expense = new Expenses { Id = Guid.NewGuid(), WorkId = workId, Amount = 12m, Description = "Train", AnalyseToken = token };
        _context.Expenses.Add(expense);
        return expense;
    }

    private Break AddBreak(Guid? token, DateOnly date)
    {
        var brk = new Break
        {
            Id = Guid.NewGuid(), AnalyseToken = token, ClientId = Guid.NewGuid(), CurrentDate = date, AbsenceId = Guid.NewGuid(),
            StartTime = Start, EndTime = End
        };
        _context.Break.Add(brk);
        return brk;
    }

    private ScheduleNote AddNote(Guid? token, DateOnly date)
    {
        var note = new ScheduleNote { Id = Guid.NewGuid(), AnalyseToken = token, ClientId = Guid.NewGuid(), CurrentDate = date, Content = "note" };
        _context.ScheduleNotes.Add(note);
        return note;
    }

    private ScheduleCommand AddCommand(Guid? token, DateOnly date)
    {
        var command = new ScheduleCommand { Id = Guid.NewGuid(), AnalyseToken = token, ClientId = Guid.NewGuid(), CurrentDate = date, CommandKeyword = "FREE" };
        _context.ScheduleCommands.Add(command);
        return command;
    }

    private ClientShiftPreference AddPreference(Guid shiftId, Guid? token)
    {
        var preference = new ClientShiftPreference { Id = Guid.NewGuid(), ClientId = Guid.NewGuid(), ShiftId = shiftId, AnalyseToken = token };
        _context.ClientShiftPreference.Add(preference);
        return preference;
    }

    private ShiftExpenses AddShiftExpense(Guid shiftId, Guid? token)
    {
        var expense = new ShiftExpenses { Id = Guid.NewGuid(), ShiftId = shiftId, Amount = 5m, Description = "Meal", AnalyseToken = token };
        _context.ShiftExpenses.Add(expense);
        return expense;
    }

    private GroupItem AddGroupItem(Guid? token, Guid? shiftId, Guid? clientId)
    {
        var item = new GroupItem { Id = Guid.NewGuid(), GroupId = Guid.NewGuid(), ShiftId = shiftId, ClientId = clientId, AnalyseToken = token };
        _context.GroupItem.Add(item);
        return item;
    }

    private ReplacementRequest AddRequest(Guid? token, ReplacementRequestOutcome outcome)
    {
        var request = new ReplacementRequest
        {
            Id = Guid.NewGuid(), AbsentClientId = Guid.NewGuid(), CandidateClientId = Guid.NewGuid(), ShiftId = Guid.NewGuid(),
            Date = Day, StartTime = Start, EndTime = End, Outcome = outcome, AnalyseToken = token
        };
        _context.ReplacementRequests.Add(request);
        return request;
    }

    private PlanningConstraint AddConstraint(Guid? token)
    {
        var constraint = new PlanningConstraint { Id = Guid.NewGuid(), ParametersJson = "{}", AnalyseToken = token };
        _context.PlanningConstraint.Add(constraint);
        return constraint;
    }
}
