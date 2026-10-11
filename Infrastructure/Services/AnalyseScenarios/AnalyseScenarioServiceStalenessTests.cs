// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests for the staleness check before an accept (FindRealPlanChangesSinceCreationAsync): real rows inside the scope the
/// accept replaces that were created, updated or deleted after the scenario was created make it stale - whether another
/// scenario was accepted in between, an entry was edited by hand or deleted. Changes outside the scope (other group,
/// outside the range, locked works, scenario rows) do not count, because the accept does not overwrite them.
/// The "created" instant is taken between two save batches, so baseline rows are older and later changes newer.
/// </summary>

using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.AnalyseScenarios;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Klacks.UnitTest.Infrastructure.Services.AnalyseScenarios;

[TestFixture]
public class AnalyseScenarioServiceStalenessTests
{
    private const int ClockGapMilliseconds = 20;

    private static readonly DateOnly FromDate = new(2026, 3, 9);
    private static readonly DateOnly UntilDate = new(2026, 3, 15);
    private static readonly DateOnly InsideDay = new(2026, 3, 10);
    private static readonly DateOnly OutsideDay = new(2026, 3, 20);

    private DataBaseContext _context = null!;
    private AnalyseScenarioService _service = null!;
    private Guid _groupId;
    private Guid _otherGroupId;
    private Guid _shiftId;
    private Guid _otherShiftId;
    private Guid _clientId;
    private Guid _otherClientId;

    [SetUp]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _service = new AnalyseScenarioService(_context);

        _groupId = Guid.NewGuid();
        _otherGroupId = Guid.NewGuid();
        _shiftId = Guid.NewGuid();
        _otherShiftId = Guid.NewGuid();
        _clientId = Guid.NewGuid();
        _otherClientId = Guid.NewGuid();

        _context.Shift.Add(new Shift { Id = _shiftId, Name = "Early" });
        _context.Shift.Add(new Shift { Id = _otherShiftId, Name = "Other group" });
        _context.Set<GroupItem>().AddRange(
            new GroupItem { Id = Guid.NewGuid(), GroupId = _groupId, ShiftId = _shiftId },
            new GroupItem { Id = Guid.NewGuid(), GroupId = _groupId, ClientId = _clientId },
            new GroupItem { Id = Guid.NewGuid(), GroupId = _otherGroupId, ShiftId = _otherShiftId },
            new GroupItem { Id = Guid.NewGuid(), GroupId = _otherGroupId, ClientId = _otherClientId });
        _context.SaveChanges();
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public async Task NothingChangedSinceCreation_IsNotStale()
    {
        var work = AddWork(_shiftId, InsideDay);
        AddWorkChange(work.Id);
        AddExpense(work.Id);
        AddBreak(_clientId, InsideDay);
        AddNote(_clientId, InsideDay);
        await _context.SaveChangesAsync();

        var scenario = await MarkScenarioCreatedAsync();

        (await _service.FindRealPlanChangesSinceCreationAsync(scenario, CancellationToken.None)).ShouldBeNull();
    }

    [Test]
    public async Task WorkEditedByHandAfterCreation_IsStale()
    {
        var work = AddWork(_shiftId, InsideDay);
        await _context.SaveChangesAsync();
        var scenario = await MarkScenarioCreatedAsync();

        work.StartTime = new TimeOnly(9, 0);
        await _context.SaveChangesAsync();

        var staleness = await _service.FindRealPlanChangesSinceCreationAsync(scenario, CancellationToken.None);

        staleness.ShouldNotBeNull();
        staleness.ChangedEntryCount.ShouldBe(1);
        staleness.LastChangeUtc.ShouldBeGreaterThan(scenario.CreateTime!.Value);
        staleness.AcceptedScenarioNames.ShouldBeEmpty();
    }

    [Test]
    public async Task BreakDeletedAfterCreation_IsStale()
    {
        var absence = AddBreak(_clientId, InsideDay);
        await _context.SaveChangesAsync();
        var scenario = await MarkScenarioCreatedAsync();

        _context.Break.Remove(absence);
        await _context.SaveChangesAsync();

        var staleness = await _service.FindRealPlanChangesSinceCreationAsync(scenario, CancellationToken.None);

        staleness.ShouldNotBeNull();
        staleness.ChangedEntryCount.ShouldBe(1);
    }

    [Test]
    public async Task ReplacementAddedToARealWorkAfterCreation_IsStale()
    {
        var work = AddWork(_shiftId, InsideDay);
        await _context.SaveChangesAsync();
        var scenario = await MarkScenarioCreatedAsync();

        AddWorkChange(work.Id);
        AddExpense(work.Id);
        await _context.SaveChangesAsync();

        var staleness = await _service.FindRealPlanChangesSinceCreationAsync(scenario, CancellationToken.None);

        staleness.ShouldNotBeNull();
        staleness.ChangedEntryCount.ShouldBe(2);
    }

    [Test]
    public async Task AnotherScenarioAcceptedAfterCreation_IsStale_AndNamesIt()
    {
        AddWork(_shiftId, InsideDay);
        AddBreak(_clientId, InsideDay);
        await _context.SaveChangesAsync();
        var siblingToken = Guid.NewGuid();
        await _service.CloneScenarioDataWithMapsAsync(_groupId, FromDate, UntilDate, siblingToken, null, null, CancellationToken.None);
        var sibling = new AnalyseScenario
        {
            Id = Guid.NewGuid(),
            Name = "Scenario B",
            Token = siblingToken,
            GroupId = _groupId,
            FromDate = FromDate,
            UntilDate = UntilDate,
            Status = AnalyseScenarioStatus.Active
        };
        _context.AnalyseScenarios.Add(sibling);
        await _context.SaveChangesAsync();
        var scenario = await MarkScenarioCreatedAsync();

        var siblingWork = await _context.Work.SingleAsync(w => w.AnalyseToken == siblingToken);
        _context.WorkChange.Add(new WorkChange
        {
            Id = Guid.NewGuid(),
            WorkId = siblingWork.Id,
            Type = WorkChangeType.ReplacementWithin,
            ReplaceClientId = _otherClientId,
            AnalyseToken = siblingToken
        });
        await _context.SaveChangesAsync();
        await _service.SoftDeleteRealScheduleDataAsync(_groupId, siblingToken, FromDate, UntilDate, CancellationToken.None);
        await _service.PromoteScenarioWorksAsync(siblingToken, FromDate, UntilDate, CancellationToken.None);
        sibling.Status = AnalyseScenarioStatus.Accepted;
        await _context.SaveChangesAsync();

        var staleness = await _service.FindRealPlanChangesSinceCreationAsync(scenario, CancellationToken.None);

        staleness.ShouldNotBeNull();
        staleness.ChangedEntryCount.ShouldBeGreaterThan(0);
        staleness.AcceptedScenarioNames.ShouldBe(new[] { "Scenario B" });
    }

    [Test]
    public async Task ChangesOutsideTheAcceptScope_AreNotStale()
    {
        var otherGroupWork = AddWork(_otherShiftId, InsideDay);
        var outsideRangeWork = AddWork(_shiftId, OutsideDay);
        var lockedWork = AddWork(_shiftId, InsideDay);
        lockedWork.LockLevel = WorkLockLevel.Confirmed;
        var otherClientBreak = AddBreak(_otherClientId, InsideDay);
        await _context.SaveChangesAsync();
        var scenario = await MarkScenarioCreatedAsync();

        otherGroupWork.StartTime = new TimeOnly(9, 0);
        outsideRangeWork.StartTime = new TimeOnly(9, 0);
        lockedWork.StartTime = new TimeOnly(9, 0);
        _context.Break.Remove(otherClientBreak);
        AddWork(_shiftId, InsideDay, analyseToken: scenario.Token);
        AddBreak(_clientId, InsideDay, analyseToken: scenario.Token);
        AddNote(_otherClientId, InsideDay);
        await _context.SaveChangesAsync();

        (await _service.FindRealPlanChangesSinceCreationAsync(scenario, CancellationToken.None)).ShouldBeNull();
    }

    [Test]
    public async Task ScenarioWithoutCreationTime_IsNotChecked()
    {
        AddWork(_shiftId, InsideDay);
        await _context.SaveChangesAsync();
        var scenario = NewScenario(createTime: null);

        (await _service.FindRealPlanChangesSinceCreationAsync(scenario, CancellationToken.None)).ShouldBeNull();
    }

    private async Task<AnalyseScenario> MarkScenarioCreatedAsync()
    {
        await Task.Delay(ClockGapMilliseconds);
        var scenario = NewScenario(DateTime.UtcNow);
        await Task.Delay(ClockGapMilliseconds);
        return scenario;
    }

    private AnalyseScenario NewScenario(DateTime? createTime) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Scenario A",
        Token = Guid.NewGuid(),
        GroupId = _groupId,
        FromDate = FromDate,
        UntilDate = UntilDate,
        Status = AnalyseScenarioStatus.Active,
        CreateTime = createTime
    };

    private Work AddWork(Guid shiftId, DateOnly date, Guid? analyseToken = null)
    {
        var work = new Work
        {
            Id = Guid.NewGuid(),
            ClientId = _clientId,
            ShiftId = shiftId,
            CurrentDate = date,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(16, 0),
            WorkTime = 8m,
            AnalyseToken = analyseToken
        };
        _context.Work.Add(work);
        return work;
    }

    private void AddWorkChange(Guid workId)
        => _context.WorkChange.Add(new WorkChange
        {
            Id = Guid.NewGuid(),
            WorkId = workId,
            Type = WorkChangeType.ReplacementWithin,
            ReplaceClientId = _otherClientId
        });

    private void AddExpense(Guid workId)
        => _context.Expenses.Add(new Expenses { Id = Guid.NewGuid(), WorkId = workId, Amount = 12m, Description = "Train" });

    private Break AddBreak(Guid clientId, DateOnly date, Guid? analyseToken = null)
    {
        var absence = new Break
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            AbsenceId = Guid.NewGuid(),
            CurrentDate = date,
            StartTime = new TimeOnly(0, 0),
            EndTime = new TimeOnly(23, 59),
            AnalyseToken = analyseToken
        };
        _context.Break.Add(absence);
        return absence;
    }

    private void AddNote(Guid clientId, DateOnly date)
        => _context.ScheduleNotes.Add(new ScheduleNote { Id = Guid.NewGuid(), ClientId = clientId, CurrentDate = date, Content = "Note" });
}
