// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The container save with the real replacement write guard: a new or changed replacement among the work changes of a
/// sub-work names a valid person like a replacement saved on its own and is checked against the stored row, a row that
/// is the same as the stored one is not checked again, and a correction needs no person.
/// </summary>

using Klacks.Api.Application.Commands.Works;
using Klacks.Api.Application.Constants;
using Klacks.Api.Application.DTOs.Notifications;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Handlers.Works;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.Works;

[TestFixture]
public class UpdateContainerWorkChildrenReplacementGuardTests
{
    private static readonly DateOnly Day = new(2027, 5, 10);
    private static readonly TimeOnly WorkStart = new(8, 0);
    private static readonly TimeOnly WorkEnd = new(16, 0);
    private static readonly TimeOnly Ten = new(10, 0);
    private static readonly TimeOnly Noon = new(12, 0);
    private const decimal HalfShiftHours = 4m;

    private IContainerWorkChildrenReadRepository _childrenReadRepository = null!;
    private IContainerWorkChildrenManager _childrenManager = null!;
    private IClientVisibilityGuard _clientVisibilityGuard = null!;
    private IClientRepository _clientRepository = null!;
    private IPreCommitConflictChecker _conflictChecker = null!;
    private Work _container = null!;
    private Work _subWork = null!;
    private Guid _personId;

    [SetUp]
    public void SetUp()
    {
        _childrenReadRepository = Substitute.For<IContainerWorkChildrenReadRepository>();
        _childrenManager = Substitute.For<IContainerWorkChildrenManager>();
        _clientVisibilityGuard = Substitute.For<IClientVisibilityGuard>();
        _clientRepository = Substitute.For<IClientRepository>();
        _conflictChecker = Substitute.For<IPreCommitConflictChecker>();
        _personId = Guid.NewGuid();
        _container = new Work
        {
            Id = Guid.NewGuid(),
            ClientId = Guid.NewGuid(),
            ShiftId = Guid.NewGuid(),
            CurrentDate = Day,
            StartTime = WorkStart,
            EndTime = WorkEnd,
        };
        _subWork = new Work
        {
            Id = Guid.NewGuid(),
            ClientId = _container.ClientId,
            ShiftId = _container.ShiftId,
            CurrentDate = Day,
            StartTime = WorkStart,
            EndTime = WorkEnd,
        };

        _clientVisibilityGuard.IsVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
        _clientVisibilityGuard.AreAllVisibleAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _clientRepository.GetTypeAndDisplayNameAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new ClientTypeAndDisplayName(EntityTypeEnum.Employee, "Substitute"));
        _conflictChecker.CheckAsync(
                Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<IReadOnlyList<PlannedRemovalRow>>(),
                Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(PreCommitCheckResult.Empty);
        _childrenReadRepository.GetParentWorkNoTracking(_container.Id, Arg.Any<CancellationToken>()).Returns(_container);
        _childrenReadRepository.GetChildWorkShiftIds(_container.Id, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, Guid> { [_subWork.Id] = _subWork.ShiftId });
        _childrenReadRepository.GetChildWorksWithShiftClient(_container.Id, Arg.Any<CancellationToken>())
            .Returns(new List<Work> { _subWork });
        _childrenReadRepository.GetChildBreaksWithAbsence(_container.Id, Arg.Any<CancellationToken>()).Returns(new List<Break>());
    }

    [Test]
    public async Task NewReplacementWithoutAPerson_IsRefused_AndNothingIsWritten()
    {
        GivenStored();

        var exception = await Should.ThrowAsync<InvalidRequestException>(
            () => Save(Resource(Requested(WorkChangeType.ReplacementWithin, null))));

        exception.Message.ShouldBe(ReplacementWriteMessages.PersonRequired);
        await WasNotWritten();
    }

    [Test]
    public async Task NewReplacementWithACustomer_IsRefused()
    {
        var customer = Guid.NewGuid();
        _clientRepository.GetTypeAndDisplayNameAsync(customer, Arg.Any<CancellationToken>())
            .Returns(new ClientTypeAndDisplayName(EntityTypeEnum.Customer, "Customer"));
        GivenStored();

        var exception = await Should.ThrowAsync<InvalidRequestException>(
            () => Save(Resource(Requested(WorkChangeType.ReplacementWithin, customer))));

        exception.Message.ShouldBe(ReplacementWriteMessages.PersonNotAnEmployee);
        await WasNotWritten();
    }

    [Test]
    public async Task NewReplacementWithAnEmployee_IsCheckedAgainstTheSubWork_AndSaved()
    {
        GivenStored();

        await Save(Resource(Requested(WorkChangeType.ReplacementWithin, _personId)));

        await _conflictChecker.Received(1).CheckAsync(
            Arg.Is<IReadOnlyList<PlannedWorkRow>>(rows => rows.Count == 1
                && rows[0].ClientId == _personId && rows[0].Date == Day && rows[0].StartTime == Ten && rows[0].EndTime == Noon),
            Arg.Is<IReadOnlyList<PlannedRemovalRow>>(removals => removals.Count == 0),
            null, Arg.Any<CancellationToken>());
        await _childrenManager.ReceivedWithAnyArgs(1).UpdateChildrenAsync(
            default, default, default, default, default, default!, default!, default!, default);
    }

    [Test]
    public async Task NewReplacementOnAScenarioContainer_NeedsAPersonButIsNotCheckedForConflicts()
    {
        _container.AnalyseToken = Guid.NewGuid();
        GivenStored();

        await Save(Resource(Requested(WorkChangeType.ReplacementWithin, _personId)));
        await Should.ThrowAsync<InvalidRequestException>(
            () => Save(Resource(Requested(WorkChangeType.ReplacementWithin, null))));

        await _conflictChecker.DidNotReceiveWithAnyArgs().CheckAsync(
            Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<IReadOnlyList<PlannedRemovalRow>>(),
            Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UnchangedStoredReplacementWithoutAPerson_DoesNotBlockTheSave()
    {
        var stored = Stored(WorkChangeType.ReplacementWithin, null);
        GivenStored(stored);

        await Save(Resource(ToRequest(stored)));

        await _clientRepository.DidNotReceiveWithAnyArgs().GetTypeAndDisplayNameAsync(default, default);
        await _childrenManager.ReceivedWithAnyArgs(1).UpdateChildrenAsync(
            default, default, default, default, default, default!, default!, default!, default);
    }

    [Test]
    public async Task UnchangedStoredReplacement_IsNotCheckedForConflictsAgain()
    {
        var stored = Stored(WorkChangeType.ReplacementWithin, _personId);
        GivenStored(stored);
        _conflictChecker.CheckAsync(
                Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<IReadOnlyList<PlannedRemovalRow>>(),
                Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new PreCommitCheckResult([EscalatedError(_personId)]));

        await Save(Resource(ToRequest(stored)));

        await _conflictChecker.DidNotReceiveWithAnyArgs().CheckAsync(
            Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<IReadOnlyList<PlannedRemovalRow>>(),
            Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task StoredReplacementChangedToAnotherPerson_IsCheckedLikeANewOne()
    {
        var stored = Stored(WorkChangeType.ReplacementWithin, _personId);
        GivenStored(stored);
        var newPerson = Guid.NewGuid();
        var request = ToRequest(stored);
        request.ReplaceClientId = newPerson;
        _conflictChecker.CheckAsync(
                Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<IReadOnlyList<PlannedRemovalRow>>(),
                Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new PreCommitCheckResult([EscalatedError(newPerson)]));

        await Should.ThrowAsync<ConflictException>(() => Save(Resource(request)));

        await WasNotWritten();
    }

    [Test]
    public async Task StoredReplacementWidened_IsCheckedWithTheWholeNewWindow_AgainstTheStoredRow()
    {
        var stored = Stored(WorkChangeType.ReplacementWithin, _personId);
        GivenStored(stored);
        var request = ToRequest(stored);
        request.EndTime = WorkEnd;

        await Save(Resource(request));

        await _conflictChecker.Received(1).CheckAsync(
            Arg.Is<IReadOnlyList<PlannedWorkRow>>(rows => rows.Count == 1 && rows[0].StartTime == Ten && rows[0].EndTime == WorkEnd),
            Arg.Is<IReadOnlyList<PlannedRemovalRow>>(removals => removals.Count == 1
                && removals[0].StartTime == Ten && removals[0].EndTime == Noon && removals[0].WorkId == stored.Id),
            null, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task StoredReplacementCutBackToACorrection_NeedsNoPerson()
    {
        var stored = Stored(WorkChangeType.ReplacementWithin, _personId);
        GivenStored(stored);
        var request = ToRequest(stored);
        request.Type = WorkChangeType.CorrectionEnd;
        request.ReplaceClientId = null;

        await Save(Resource(request));

        await _childrenManager.ReceivedWithAnyArgs(1).UpdateChildrenAsync(
            default, default, default, default, default, default!, default!, default!, default);
    }

    [Test]
    public async Task ReplacementOnAForeignWork_IsAnsweredLikeAMissingContainer_AndNothingIsWritten()
    {
        GivenStored();
        var foreign = Requested(WorkChangeType.ReplacementWithin, _personId);
        foreign.WorkId = Guid.NewGuid();

        var exception = await Should.ThrowAsync<KeyNotFoundException>(() => Save(Resource(foreign)));

        exception.Message.ShouldBe(string.Format(ContainerLockMessages.WorkNotFound, _container.Id));
        await WasNotWritten();
    }

    [Test]
    public async Task CorrectionOnAForeignWork_IsRefusedToo()
    {
        GivenStored();
        var foreign = Requested(WorkChangeType.CorrectionEnd, null);
        foreign.WorkId = Guid.NewGuid();

        await Should.ThrowAsync<KeyNotFoundException>(() => Save(Resource(foreign)));

        await WasNotWritten();
    }

    [Test]
    public async Task StoredChangeMovedToAForeignWork_IsRefused()
    {
        var stored = Stored(WorkChangeType.ReplacementWithin, _personId);
        GivenStored(stored);
        var moved = ToRequest(stored);
        moved.WorkId = Guid.NewGuid();

        await Should.ThrowAsync<KeyNotFoundException>(() => Save(Resource(moved)));

        await WasNotWritten();
    }

    [Test]
    public async Task ChangeOnTheContainerItself_IsRefused()
    {
        GivenStored();
        var onContainer = Requested(WorkChangeType.CorrectionEnd, null);
        onContainer.WorkId = _container.Id;

        await Should.ThrowAsync<KeyNotFoundException>(() => Save(Resource(onContainer)));

        await WasNotWritten();
    }

    [Test]
    public async Task ChangeWithAnEmptyWorkId_IsRefused()
    {
        GivenStored();
        var empty = Requested(WorkChangeType.CorrectionEnd, null);
        empty.WorkId = Guid.Empty;

        await Should.ThrowAsync<KeyNotFoundException>(() => Save(Resource(empty)));

        await WasNotWritten();
    }

    [Test]
    public async Task ChangeOnANewSubWorkOfTheRequest_IsAccepted()
    {
        GivenStored();
        var resource = Resource(Requested(WorkChangeType.CorrectionEnd, null));
        var newSubWorkId = Guid.NewGuid();
        resource.SubWorks.Add(new WorkResource
        {
            Id = newSubWorkId, ClientId = _subWork.ClientId, ShiftId = _subWork.ShiftId, CurrentDate = Day,
            StartTime = WorkStart, EndTime = WorkEnd,
        });
        resource.SubWorkChanges[0].WorkId = newSubWorkId;

        await Save(resource);

        await _childrenManager.ReceivedWithAnyArgs(1).UpdateChildrenAsync(
            default, default, default, default, default, default!, default!, default!, default);
    }

    [Test]
    public async Task ReplacementOnAStoredSubWorkThatTheRequestDoesNotName_IsCheckedAgainstThatSubWork()
    {
        var otherStored = new Work
        {
            Id = Guid.NewGuid(), ClientId = _container.ClientId, ShiftId = _container.ShiftId, CurrentDate = Day,
            StartTime = new TimeOnly(18, 0), EndTime = new TimeOnly(22, 0),
        };
        _childrenReadRepository.GetChildWorkShiftIds(_container.Id, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, Guid> { [_subWork.Id] = _subWork.ShiftId, [otherStored.Id] = otherStored.ShiftId });
        _childrenReadRepository.GetChildWorksWithShiftClient(_container.Id, Arg.Any<CancellationToken>())
            .Returns(new List<Work> { _subWork, otherStored });
        GivenStored();
        var change = Requested(WorkChangeType.ReplacementStart, _personId);
        change.WorkId = otherStored.Id;
        change.StartTime = default;
        change.EndTime = default;

        await Save(Resource(change));

        await _conflictChecker.Received(1).CheckAsync(
            Arg.Is<IReadOnlyList<PlannedWorkRow>>(rows => rows.Count == 1
                && rows[0].StartTime == new TimeOnly(18, 0) && rows[0].EndTime == new TimeOnly(22, 0)),
            Arg.Any<IReadOnlyList<PlannedRemovalRow>>(), null, Arg.Any<CancellationToken>());
    }

    private Task Save(UpdateContainerWorkChildrenResource resource)
        => NewHandler().Handle(new UpdateContainerWorkChildrenCommand(_container.Id, resource), CancellationToken.None);

    private async Task WasNotWritten()
        => await _childrenManager.DidNotReceiveWithAnyArgs().UpdateChildrenAsync(
            default, default, default, default, default, default!, default!, default!, default);

    private void GivenStored(params WorkChange[] stored)
        => _childrenReadRepository.GetWorkChangesForWorks(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(stored.ToList());

    private UpdateContainerWorkChildrenResource Resource(params WorkChangeResource[] changes)
    {
        var resource = new UpdateContainerWorkChildrenResource();
        resource.SubWorks.Add(new WorkResource
        {
            Id = _subWork.Id,
            ClientId = _subWork.ClientId,
            ShiftId = _subWork.ShiftId,
            CurrentDate = Day,
            StartTime = WorkStart,
            EndTime = WorkEnd,
        });
        resource.SubWorkChanges.AddRange(changes);
        return resource;
    }

    private WorkChange Stored(WorkChangeType type, Guid? personId) => new()
    {
        Id = Guid.NewGuid(),
        WorkId = _subWork.Id,
        Type = type,
        ReplaceClientId = personId,
        StartTime = Ten,
        EndTime = Noon,
        ChangeTime = HalfShiftHours,
    };

    private WorkChangeResource Requested(WorkChangeType type, Guid? personId) => new()
    {
        WorkId = _subWork.Id,
        Type = type,
        ReplaceClientId = personId,
        StartTime = Ten,
        EndTime = Noon,
        ChangeTime = HalfShiftHours,
    };

    private static WorkChangeResource ToRequest(WorkChange stored) => new()
    {
        Id = stored.Id,
        WorkId = stored.WorkId,
        Type = stored.Type,
        ReplaceClientId = stored.ReplaceClientId,
        StartTime = stored.StartTime,
        EndTime = stored.EndTime,
        ChangeTime = stored.ChangeTime,
    };

    private UpdateContainerWorkChildrenCommandHandler NewHandler()
    {
        var lockRepository = Substitute.For<IContainerLockRepository>();
        lockRepository.IsHeldBy(
                Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);
        return new UpdateContainerWorkChildrenCommandHandler(
            _childrenReadRepository, TestScheduleRowAccess.Over(_clientVisibilityGuard), new ScheduleMapper(),
            Substitute.For<IUnitOfWork>(), Substitute.For<IWorkNotificationFacade>(), lockRepository,
            Substitute.For<IUserService>(), _childrenManager, Substitute.For<IOvertimeCascadeService>(),
            ShiftVisibilityGuards.AllowAll(), Substitute.For<IWorkWriteGuard>(),
            new ReplacementWriteGuard(_clientRepository, _conflictChecker, Substitute.For<ISupervisorOverrideAuthorizer>(), Substitute.For<IContainerWorkChangeGuard>()),
            Substitute.For<IContainerWorkChangeGuard>(),
            Substitute.For<IContainerChildrenWriteGuard>(),
            Substitute.For<ILogger<UpdateContainerWorkChildrenCommandHandler>>());
    }

    private static ScheduleValidationNotificationDto EscalatedError(Guid clientId) => new()
    {
        Type = ScheduleValidationType.Error,
        ClientId = clientId,
        Date = Day,
        Comment = "schedule.error-list.rest-violation",
        CommentParams = new Dictionary<string, string>
        {
            [ComplianceRuleNames.EnforcementRuleParamKey] = ComplianceRuleNames.MinRestHours,
        },
    };
}
