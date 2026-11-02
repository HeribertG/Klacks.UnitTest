// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// What a container save has to pass for its children: the seal state, the scenario token and the clone link stay with
/// the stored row, the lock levels and the day locks of old and new data hold, a child that is left out is deleted only
/// where a delete is allowed, and a new child may take an id of its own as long as no row has taken it.
/// </summary>

using System.Security.Claims;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Services.Schedules;
using Microsoft.AspNetCore.Http;

namespace Klacks.UnitTest.Application.Services.Schedules;

[TestFixture]
public class ContainerChildrenWriteGuardTests
{
    private static readonly DateOnly Day = new(2027, 5, 10);
    private static readonly DateTime SealedAt = new(2027, 5, 11, 8, 0, 0, DateTimeKind.Utc);
    private static readonly TimeOnly Eight = new(8, 0);
    private static readonly TimeOnly Sixteen = new(16, 0);

    private IContainerWorkChildrenReadRepository _readRepository = null!;
    private IDayLockService _dayLockService = null!;
    private IHttpContextAccessor _httpContextAccessor = null!;
    private IShiftRepository _shiftRepository = null!;
    private ContainerChildrenWriteGuard _guard = null!;
    private Work _container = null!;
    private Guid _scenarioToken;
    private IReadOnlyCollection<(DateOnly Date, Guid ClientId, Guid? AnalyseToken)> _checkedDays = [];
    private IReadOnlyCollection<(DateOnly Date, Guid ClientId)> _checkedDeletes = [];

    [SetUp]
    public void SetUp()
    {
        _readRepository = Substitute.For<IContainerWorkChildrenReadRepository>();
        _dayLockService = Substitute.For<IDayLockService>();
        _httpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _shiftRepository = Substitute.For<IShiftRepository>();
        _shiftRepository.GetWithClientByIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<Shift>());
        _scenarioToken = Guid.NewGuid();
        _container = new Work
        {
            Id = Guid.NewGuid(), ClientId = Guid.NewGuid(), ShiftId = Guid.NewGuid(), CurrentDate = Day,
            StartTime = Eight, EndTime = Sixteen,
        };
        _readRepository.GetUsedEntryIds(
                Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<IReadOnlyCollection<Guid>>(),
                Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new UsedEntryIds(new HashSet<Guid>(), new HashSet<Guid>(), new HashSet<Guid>()));
        _dayLockService.EnsureNoneLockedAsync(
                Arg.Do<IReadOnlyCollection<(DateOnly Date, Guid ClientId, Guid? AnalyseToken)>>(days => _checkedDays = days),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _dayLockService.EnsureDeletableAsync(
                Arg.Do<IReadOnlyCollection<(DateOnly Date, Guid ClientId)>>(works => _checkedDeletes = works),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        GivenRole(null);
        _guard = new ContainerChildrenWriteGuard(
            _readRepository, new ParentWorkLockGuard(new WorkLockLevelService()), _dayLockService, _httpContextAccessor,
            _shiftRepository);
    }

    [Test]
    public async Task StoredSealState_StaysWithTheStoredWork_WhateverTheRebuiltEntityCarries()
    {
        var storedWork = SealedWork(WorkLockLevel.Confirmed);
        var rebuilt = Rebuilt(storedWork);

        await Prepare([storedWork], [], [], [rebuilt], [], []);

        rebuilt.LockLevel.ShouldBe(WorkLockLevel.Confirmed);
        rebuilt.SealedAt.ShouldBe(SealedAt);
        rebuilt.SealedBy.ShouldBe("supervisor");
        rebuilt.PreSealLockLevel.ShouldBe(WorkLockLevel.None);
        rebuilt.PreSealSealedAt.ShouldBe(SealedAt.AddDays(-1));
        rebuilt.PreSealSealedBy.ShouldBe("earlier");
    }

    [Test]
    public async Task StoredSealState_StaysWithTheStoredBreak_IncludingTheSealingGroup()
    {
        var group = Guid.NewGuid();
        var storedBreak = new Break
        {
            Id = Guid.NewGuid(), ClientId = _container.ClientId, CurrentDate = Day, ParentWorkId = _container.Id,
            LockLevel = WorkLockLevel.Confirmed, SealedAt = SealedAt, SealedBy = "supervisor",
            SealedByGroupId = group, PreSealSealedByGroupId = Guid.NewGuid(),
        };
        var rebuilt = new Break { Id = storedBreak.Id, ClientId = Guid.NewGuid(), CurrentDate = Day };

        await Prepare([], [storedBreak], [], [], [rebuilt], []);

        rebuilt.LockLevel.ShouldBe(WorkLockLevel.Confirmed);
        rebuilt.SealedByGroupId.ShouldBe(group);
        rebuilt.PreSealSealedByGroupId.ShouldBe(storedBreak.PreSealSealedByGroupId);
    }

    [Test]
    public async Task StoredWork_KeepsItsScenarioAndCloneLink_AndTheRequestCannotChangeThem()
    {
        var origin = Guid.NewGuid();
        var storedWork = SealedWork(WorkLockLevel.None);
        storedWork.AnalyseToken = _scenarioToken;
        storedWork.ClonedFromWorkId = origin;
        var rebuilt = Rebuilt(storedWork);
        rebuilt.AnalyseToken = Guid.NewGuid();
        rebuilt.ClonedFromWorkId = Guid.NewGuid();
        rebuilt.ParentWorkId = Guid.NewGuid();

        await Prepare([storedWork], [], [], [rebuilt], [], []);

        rebuilt.AnalyseToken.ShouldBe(_scenarioToken);
        rebuilt.ClonedFromWorkId.ShouldBe(origin);
        rebuilt.ParentWorkId.ShouldBe(_container.Id);
    }

    [Test]
    public async Task NewChildren_TakeTheScenarioOfTheContainer_AndStartUnsealed()
    {
        _container.AnalyseToken = _scenarioToken;
        var newWork = new Work
        {
            Id = Guid.NewGuid(), ClientId = _container.ClientId, CurrentDate = Day, AnalyseToken = Guid.NewGuid(),
            LockLevel = WorkLockLevel.Closed, ClonedFromWorkId = Guid.NewGuid(),
        };
        var newBreak = new Break { Id = Guid.NewGuid(), ClientId = Guid.NewGuid(), AnalyseToken = Guid.NewGuid() };
        var newChange = new WorkChange { Id = Guid.NewGuid(), WorkId = newWork.Id, AnalyseToken = Guid.NewGuid() };

        await Prepare([], [], [], [newWork], [newBreak], [newChange]);

        newWork.AnalyseToken.ShouldBe(_scenarioToken);
        newWork.LockLevel.ShouldBe(WorkLockLevel.None);
        newWork.ClonedFromWorkId.ShouldBeNull();
        newWork.ParentWorkId.ShouldBe(_container.Id);
        newBreak.AnalyseToken.ShouldBe(_scenarioToken);
        newBreak.ParentWorkId.ShouldBe(_container.Id);
        newChange.AnalyseToken.ShouldBe(_scenarioToken);
    }

    [Test]
    public async Task EveryWorkChange_IsAttachedToTheScenarioOfTheContainer_AlsoAStoredOne()
    {
        _container.AnalyseToken = _scenarioToken;
        var storedWork = SealedWork(WorkLockLevel.None);
        storedWork.AnalyseToken = _scenarioToken;
        var storedChange = new WorkChange { Id = Guid.NewGuid(), WorkId = storedWork.Id, AnalyseToken = _scenarioToken };
        var rebuiltChange = new WorkChange { Id = storedChange.Id, WorkId = storedWork.Id };

        await Prepare([storedWork], [], [storedChange], [Rebuilt(storedWork)], [], [rebuiltChange]);

        rebuiltChange.AnalyseToken.ShouldBe(_scenarioToken);
    }

    [Test]
    public async Task NewBreak_TakesTheEmployeeAndTheDayOfTheContainer()
    {
        var newBreak = new Break { Id = Guid.NewGuid(), ClientId = Guid.NewGuid() };

        await Prepare([], [], [], [], [newBreak], []);

        newBreak.ClientId.ShouldBe(_container.ClientId);
        newBreak.CurrentDate.ShouldBe(Day);
    }

    [TestCase(WorkLockLevel.Closed, null)]
    [TestCase(WorkLockLevel.Closed, Roles.Admin)]
    [TestCase(WorkLockLevel.Approved, null)]
    public async Task StoredWorkThatTheCallerMayNotWrite_RefusesTheSave(WorkLockLevel level, string? role)
    {
        GivenRole(role);
        var storedWork = SealedWork(level);

        await Should.ThrowAsync<InvalidRequestException>(
            () => Prepare([storedWork], [], [], [Rebuilt(storedWork)], [], []));
    }

    [TestCase(WorkLockLevel.Approved, Roles.Admin)]
    [TestCase(WorkLockLevel.Approved, Roles.Authorised)]
    [TestCase(WorkLockLevel.Confirmed, null)]
    public async Task StoredWorkThatTheCallerMayWrite_IsAccepted(WorkLockLevel level, string? role)
    {
        GivenRole(role);
        var storedWork = SealedWork(level);

        await Prepare([storedWork], [], [], [Rebuilt(storedWork)], [], []);
    }

    [Test]
    public async Task ChildLeftOutOfTheRequest_IsDeletedOnlyWhereADeleteIsAllowed()
    {
        var closed = SealedWork(WorkLockLevel.Closed);

        await Should.ThrowAsync<InvalidRequestException>(() => Prepare([closed], [], [], [], [], []));
    }

    [Test]
    public async Task ClosedBreak_RefusesTheSaveForANonAdmin_AlsoWhenItIsLeftOut()
    {
        var closedBreak = new Break
        {
            Id = Guid.NewGuid(), ClientId = _container.ClientId, CurrentDate = Day, ParentWorkId = _container.Id,
            LockLevel = WorkLockLevel.Closed,
        };

        await Should.ThrowAsync<InvalidRequestException>(() => Prepare([], [closedBreak], [], [], [], []));
        GivenRole(Roles.Admin);
        await Prepare([], [closedBreak], [], [], [], []);
    }

    [Test]
    public async Task LockedContainer_RefusesTheSave()
    {
        _container.LockLevel = WorkLockLevel.Closed;

        await Should.ThrowAsync<InvalidRequestException>(() => Prepare([], [], [], [], [], []));
    }

    [Test]
    public async Task DaysOfTheContainerTheStoredAndTheNewChildren_AreCheckedForSeals()
    {
        var otherDay = Day.AddDays(1);
        var storedWork = SealedWork(WorkLockLevel.None);
        var moved = Rebuilt(storedWork);
        moved.CurrentDate = otherDay;
        var newBreak = new Break { Id = Guid.NewGuid() };

        await Prepare([storedWork], [], [], [moved], [newBreak], []);

        _checkedDays.ShouldContain((Day, _container.ClientId, (Guid?)null));
        _checkedDays.ShouldContain((Day, storedWork.ClientId, (Guid?)null));
        _checkedDays.ShouldContain((otherDay, moved.ClientId, (Guid?)null));
    }

    [Test]
    public async Task SealedDay_RefusesTheSave()
    {
        _dayLockService.EnsureNoneLockedAsync(
                Arg.Any<IReadOnlyCollection<(DateOnly Date, Guid ClientId, Guid? AnalyseToken)>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidRequestException("Day is sealed")));

        await Should.ThrowAsync<InvalidRequestException>(() => Prepare([], [], [], [], [], []));
    }

    [Test]
    public async Task WorkLeftOutInAScenario_AlsoPassesTheDeleteCheckOfSealedDays()
    {
        _container.AnalyseToken = _scenarioToken;
        var storedWork = SealedWork(WorkLockLevel.None);
        storedWork.AnalyseToken = _scenarioToken;

        await Prepare([storedWork], [], [], [], [], []);

        _checkedDeletes.ShouldContain((Day, storedWork.ClientId));
    }

    [Test]
    public async Task WorkLeftOutInTheRealPlan_NeedsNoDeleteCheckOfScenarios()
    {
        var storedWork = SealedWork(WorkLockLevel.None);

        await Prepare([storedWork], [], [], [], [], []);

        await _dayLockService.DidNotReceiveWithAnyArgs().EnsureDeletableAsync(default!, default);
    }

    [Test]
    public async Task NewAndChangedWorkChanges_AndLeftOutOnes_AreCheckedAgainstTheDayTheyAreShownOn_InOneSealCheck()
    {
        var storedWork = SealedWork(WorkLockLevel.None);
        var unchanged = Change(storedWork.Id, Guid.NewGuid(), Guid.NewGuid());
        var changedStored = Change(storedWork.Id, Guid.NewGuid(), Guid.NewGuid());
        var leftOut = Change(storedWork.Id, Guid.NewGuid(), Guid.NewGuid());
        var rebuiltUnchanged = Clone(unchanged);
        var rebuiltChanged = Clone(changedStored);
        rebuiltChanged.EndTime = new TimeOnly(18, 0);
        var brandNew = Change(storedWork.Id, Guid.Empty, Guid.NewGuid());

        await Prepare(
            [storedWork], [], [unchanged, changedStored, leftOut],
            [Rebuilt(storedWork)], [], [rebuiltUnchanged, rebuiltChanged, brandNew]);

        _checkedDays.ShouldContain((Day, leftOut.ReplaceClientId!.Value, (Guid?)null));
        _checkedDays.ShouldContain((Day, rebuiltChanged.ReplaceClientId!.Value, (Guid?)null));
        _checkedDays.ShouldContain((Day, brandNew.ReplaceClientId!.Value, (Guid?)null));
        _checkedDays.ShouldNotContain((Day, rebuiltUnchanged.ReplaceClientId!.Value, (Guid?)null));
        await _dayLockService.DidNotReceiveWithAnyArgs().EnsureWorkChangeNotLockedAsync(default!, default!, default);
        await _dayLockService.Received(1).EnsureNoneLockedAsync(
            Arg.Any<IReadOnlyCollection<(DateOnly Date, Guid ClientId, Guid? AnalyseToken)>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ManyWorkChanges_ReadTheShiftsOnce()
    {
        var storedWork = SealedWork(WorkLockLevel.None);
        var otherWork = SealedWork(WorkLockLevel.None);
        otherWork.ShiftId = Guid.NewGuid();
        var changes = Enumerable.Range(0, 40)
            .Select(index => Change(index % 2 == 0 ? storedWork.Id : otherWork.Id, Guid.Empty, null))
            .ToList();

        await Prepare([storedWork, otherWork], [], [], [Rebuilt(storedWork), Rebuilt(otherWork)], [], changes);

        await _shiftRepository.Received(1).GetWithClientByIdsAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == 2), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task WorkChangeOfANightShift_IsPlacedOnTheFollowingDay_ForTheEmployeeAndTheReplacement()
    {
        var storedWork = SealedWork(WorkLockLevel.None);
        storedWork.StartTime = new TimeOnly(22, 0);
        storedWork.EndTime = new TimeOnly(6, 0);
        _shiftRepository.GetWithClientByIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<Shift> { new() { Id = storedWork.ShiftId, StartShift = new TimeOnly(22, 0), EndShift = new TimeOnly(6, 0) } });
        var replacement = Guid.NewGuid();
        var afterMidnight = new WorkChange
        {
            Id = Guid.Empty, WorkId = storedWork.Id, Type = WorkChangeType.ReplacementWithin, ReplaceClientId = replacement,
            StartTime = new TimeOnly(2, 0), EndTime = new TimeOnly(5, 0), ChangeTime = 3m,
        };

        await Prepare([storedWork], [], [], [Rebuilt(storedWork)], [], [afterMidnight]);

        _checkedDays.ShouldContain((Day.AddDays(1), storedWork.ClientId, (Guid?)null));
        _checkedDays.ShouldContain((Day.AddDays(1), replacement, (Guid?)null));
    }

    [Test]
    public async Task WorkChangeOfAStoredSubWork_IsDayLockedForTheStoredEmployee_NotTheOneTheRequestNames()
    {
        var storedWork = SealedWork(WorkLockLevel.None);
        storedWork.StartTime = new TimeOnly(22, 0);
        storedWork.EndTime = new TimeOnly(6, 0);
        _shiftRepository.GetWithClientByIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<Shift> { new() { Id = storedWork.ShiftId, StartShift = new TimeOnly(22, 0), EndShift = new TimeOnly(6, 0) } });
        var rebuilt = Rebuilt(storedWork);
        var otherEmployee = Guid.NewGuid();
        rebuilt.ClientId = otherEmployee;
        var afterMidnight = Change(storedWork.Id, Guid.Empty, null);
        afterMidnight.Type = WorkChangeType.CorrectionEnd;

        await Prepare([storedWork], [], [], [rebuilt], [], [afterMidnight]);

        _checkedDays.ShouldContain((Day.AddDays(1), storedWork.ClientId, (Guid?)null));
        _checkedDays.ShouldNotContain((Day.AddDays(1), otherEmployee, (Guid?)null));
    }

    [Test]
    public async Task WorkChangeOfAScenarioSubWork_IsNotDayLocked()
    {
        _container.AnalyseToken = _scenarioToken;
        var storedWork = SealedWork(WorkLockLevel.None);
        storedWork.AnalyseToken = _scenarioToken;
        var replacement = Guid.NewGuid();

        await Prepare([storedWork], [], [], [Rebuilt(storedWork)], [], [Change(storedWork.Id, Guid.Empty, replacement)]);

        _checkedDays.ShouldNotContain((Day, replacement, (Guid?)null));
        await _shiftRepository.DidNotReceiveWithAnyArgs().GetWithClientByIdsAsync(default!, default);
    }

    [Test]
    public async Task NewChildWithAnIdOfItsOwn_IsAcceptedWhileNoRowHasTakenTheId()
    {
        var ownId = Guid.NewGuid();
        var newWork = new Work { Id = ownId, ClientId = _container.ClientId, CurrentDate = Day };

        await Prepare([], [], [], [newWork], [], []);

        await _readRepository.Received(1).GetUsedEntryIds(
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(ownId)),
            Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task NewWorkWithTheIdOfTheContainerOrOfAForeignWork_IsAnsweredLikeAMissingContainer()
    {
        var foreignId = Guid.NewGuid();
        _readRepository.GetUsedEntryIds(
                Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<IReadOnlyCollection<Guid>>(),
                Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new UsedEntryIds(new HashSet<Guid> { _container.Id, foreignId }, new HashSet<Guid>(), new HashSet<Guid>()));

        var exception = await Should.ThrowAsync<KeyNotFoundException>(() => Prepare(
            [], [], [], [new Work { Id = _container.Id, CurrentDate = Day }, new Work { Id = foreignId, CurrentDate = Day }], [], []));

        exception.Message.ShouldBe(string.Format(ContainerLockMessages.WorkNotFound, _container.Id));
    }

    [Test]
    public async Task NewBreakOrWorkChangeWithAnIdAForeignRowHasTaken_IsAnsweredLikeAMissingContainer()
    {
        var foreignId = Guid.NewGuid();
        _readRepository.GetUsedEntryIds(
                Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<IReadOnlyCollection<Guid>>(),
                Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new UsedEntryIds(new HashSet<Guid>(), new HashSet<Guid> { foreignId }, new HashSet<Guid>()));

        await Should.ThrowAsync<KeyNotFoundException>(() => Prepare([], [], [], [], [new Break { Id = foreignId }], []));
    }

    [Test]
    public async Task StoredChildren_AreNotAskedForTheirIds()
    {
        var storedWork = SealedWork(WorkLockLevel.None);

        await Prepare([storedWork], [], [], [Rebuilt(storedWork), new Work { Id = Guid.Empty, CurrentDate = Day }], [], []);

        await _readRepository.DidNotReceiveWithAnyArgs().GetUsedEntryIds(default!, default!, default!, default);
    }

    private Task Prepare(
        IReadOnlyList<Work> storedWorks,
        IReadOnlyList<Break> storedBreaks,
        IReadOnlyList<WorkChange> storedChanges,
        IReadOnlyList<Work> works,
        IReadOnlyList<Break> breaks,
        IReadOnlyList<WorkChange> changes)
        => _guard.PrepareAsync(
            _container, new StoredContainerChildren(storedWorks, storedBreaks, storedChanges), works, breaks, changes, CancellationToken.None);

    private Work SealedWork(WorkLockLevel level) => new()
    {
        Id = Guid.NewGuid(), ClientId = Guid.NewGuid(), ShiftId = _container.ShiftId, CurrentDate = Day,
        StartTime = Eight, EndTime = Sixteen, ParentWorkId = _container.Id,
        LockLevel = level, SealedAt = SealedAt, SealedBy = "supervisor",
        PreSealLockLevel = WorkLockLevel.None, PreSealSealedAt = SealedAt.AddDays(-1), PreSealSealedBy = "earlier",
    };

    private static Work Rebuilt(Work stored) => new()
    {
        Id = stored.Id, ClientId = stored.ClientId, ShiftId = stored.ShiftId, CurrentDate = stored.CurrentDate,
        StartTime = stored.StartTime, EndTime = stored.EndTime,
    };

    private static WorkChange Change(Guid workId, Guid id, Guid? replaceClientId) => new()
    {
        Id = id, WorkId = workId, Type = WorkChangeType.CorrectionEnd, StartTime = Eight, EndTime = Sixteen, ChangeTime = 1m,
        ReplaceClientId = replaceClientId,
    };

    private static WorkChange Clone(WorkChange change) => new()
    {
        Id = change.Id, WorkId = change.WorkId, Type = change.Type, StartTime = change.StartTime,
        EndTime = change.EndTime, ChangeTime = change.ChangeTime, ReplaceClientId = change.ReplaceClientId,
    };

    private void GivenRole(string? role)
    {
        var claims = role == null ? [] : new[] { new Claim(ClaimTypes.Role, role) };
        _httpContextAccessor.HttpContext.Returns(new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth")),
        });
    }
}
