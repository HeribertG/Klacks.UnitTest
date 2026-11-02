// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The check every replacement WorkChange passes before it is written: it names an existing employee or external
/// employee, and an update that keeps the person and the occupied time is not checked for conflicts again.
/// </summary>

using Klacks.Api.Application.Constants;
using Klacks.Api.Application.DTOs.Notifications;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;

namespace Klacks.UnitTest.Application.Services.Schedules;

[TestFixture]
public class ReplacementWriteGuardTests
{
    private static readonly DateOnly Day = new(2027, 5, 10);
    private static readonly TimeOnly WorkStart = new(8, 0);
    private static readonly TimeOnly WorkEnd = new(16, 0);
    private static readonly TimeOnly Ten = new(10, 0);
    private static readonly TimeOnly Eleven = new(11, 0);
    private static readonly TimeOnly Noon = new(12, 0);
    private static readonly TimeOnly TwoPm = new(14, 0);
    private static readonly TimeOnly ThreePm = new(15, 0);
    private const decimal HalfShiftHours = 4m;

    private IClientRepository _clientRepository = null!;
    private IPreCommitConflictChecker _conflictChecker = null!;
    private ISupervisorOverrideAuthorizer _overrideAuthorizer = null!;
    private ReplacementWriteGuard _guard = null!;
    private Guid _personId;
    private Work _parentWork = null!;

    [SetUp]
    public void SetUp()
    {
        _clientRepository = Substitute.For<IClientRepository>();
        _conflictChecker = Substitute.For<IPreCommitConflictChecker>();
        _overrideAuthorizer = Substitute.For<ISupervisorOverrideAuthorizer>();
        _personId = Guid.NewGuid();
        _parentWork = new Work
        {
            Id = Guid.NewGuid(),
            ClientId = Guid.NewGuid(),
            ShiftId = Guid.NewGuid(),
            CurrentDate = Day,
            StartTime = WorkStart,
            EndTime = WorkEnd,
        };

        _clientRepository.GetTypeAndDisplayNameAsync(_personId, Arg.Any<CancellationToken>())
            .Returns(new ClientTypeAndDisplayName(EntityTypeEnum.Employee, "Substitute"));
        _conflictChecker.CheckAsync(
                Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(PreCommitCheckResult.Empty);
        _conflictChecker.CheckAsync(
                Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<IReadOnlyList<PlannedRemovalRow>>(),
                Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(PreCommitCheckResult.Empty);
        _guard = new ReplacementWriteGuard(_clientRepository, _conflictChecker, _overrideAuthorizer);
    }

    [TestCase(WorkChangeType.ReplacementStart)]
    [TestCase(WorkChangeType.ReplacementEnd)]
    [TestCase(WorkChangeType.ReplacementWithin)]
    public async Task ReplacementWithoutAPerson_IsRefused(WorkChangeType type)
    {
        var exception = await Should.ThrowAsync<InvalidRequestException>(
            () => _guard.EnsureAllowedAsync(Change(type, null), _parentWork, false, null, CancellationToken.None));

        exception.Message.ShouldBe(ReplacementWriteMessages.PersonRequired);
    }

    [Test]
    public async Task ReplacementWithAnEmptyPersonId_IsRefused()
    {
        var exception = await Should.ThrowAsync<InvalidRequestException>(
            () => _guard.EnsureAllowedAsync(
                Change(WorkChangeType.ReplacementWithin, Guid.Empty), _parentWork, false, null, CancellationToken.None));

        exception.Message.ShouldBe(ReplacementWriteMessages.PersonRequired);
    }

    [Test]
    public async Task ReplacementWithAnUnknownOrDeletedPerson_IsRefused()
    {
        var unknown = Guid.NewGuid();
        _clientRepository.GetTypeAndDisplayNameAsync(unknown, Arg.Any<CancellationToken>())
            .Returns((ClientTypeAndDisplayName?)null);

        var exception = await Should.ThrowAsync<InvalidRequestException>(
            () => _guard.EnsureAllowedAsync(
                Change(WorkChangeType.ReplacementWithin, unknown), _parentWork, false, null, CancellationToken.None));

        exception.Message.ShouldBe(ReplacementWriteMessages.PersonNotFound);
    }

    [Test]
    public async Task ReplacementWithACustomer_IsRefused()
    {
        var customer = Guid.NewGuid();
        _clientRepository.GetTypeAndDisplayNameAsync(customer, Arg.Any<CancellationToken>())
            .Returns(new ClientTypeAndDisplayName(EntityTypeEnum.Customer, "Customer"));

        var exception = await Should.ThrowAsync<InvalidRequestException>(
            () => _guard.EnsureAllowedAsync(
                Change(WorkChangeType.ReplacementWithin, customer), _parentWork, false, null, CancellationToken.None));

        exception.Message.ShouldBe(ReplacementWriteMessages.PersonNotAnEmployee);
    }

    [TestCase(EntityTypeEnum.Employee)]
    [TestCase(EntityTypeEnum.ExternEmp)]
    public async Task ReplacementWithAnEmployeeOrAnExternalEmployee_IsAllowedAndChecked(EntityTypeEnum type)
    {
        _clientRepository.GetTypeAndDisplayNameAsync(_personId, Arg.Any<CancellationToken>())
            .Returns(new ClientTypeAndDisplayName(type, "Substitute"));

        await _guard.EnsureAllowedAsync(Change(WorkChangeType.ReplacementWithin, _personId), _parentWork, false, null, CancellationToken.None);

        await _conflictChecker.Received(1).CheckAsync(
            Arg.Is<IReadOnlyList<PlannedWorkRow>>(rows => rows.Count == 1 && rows[0].ClientId == _personId),
            null, Arg.Any<CancellationToken>());
    }

    [TestCase(WorkChangeType.CorrectionEnd)]
    [TestCase(WorkChangeType.CorrectionStart)]
    [TestCase(WorkChangeType.Briefing)]
    public async Task CorrectionWithoutAPerson_StaysAllowed(WorkChangeType type)
    {
        await _guard.EnsureAllowedAsync(Change(type, null), _parentWork, false, null, CancellationToken.None);

        await _conflictChecker.DidNotReceiveWithAnyArgs().CheckAsync(
            Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ScenarioReplacement_NeedsAPersonButSkipsTheConflictCheck()
    {
        var scenario = Change(WorkChangeType.ReplacementWithin, _personId);
        scenario.AnalyseToken = Guid.NewGuid();

        await _guard.EnsureAllowedAsync(scenario, _parentWork, false, null, CancellationToken.None);
        await Should.ThrowAsync<InvalidRequestException>(() => _guard.EnsureAllowedAsync(
            Change(WorkChangeType.ReplacementWithin, null, Guid.NewGuid()), _parentWork, false, null, CancellationToken.None));

        await _conflictChecker.DidNotReceiveWithAnyArgs().CheckAsync(
            Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RefusedPerson_IsNeverChecked_ForConflicts()
    {
        await Should.ThrowAsync<InvalidRequestException>(() => _guard.EnsureAllowedAsync(
            Change(WorkChangeType.ReplacementWithin, null), _parentWork, false, null, CancellationToken.None));

        await _conflictChecker.DidNotReceiveWithAnyArgs().CheckAsync(
            Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UpdateThatKeepsPersonAndTime_IsNotCheckedForConflictsAgain()
    {
        var stored = Change(WorkChangeType.ReplacementWithin, _personId);
        var updated = Change(WorkChangeType.ReplacementWithin, _personId);
        updated.Description = "changed text only";
        updated.ToInvoice = true;

        await _guard.EnsureAllowedAsync(updated, _parentWork, false, stored, CancellationToken.None);

        await _conflictChecker.DidNotReceiveWithAnyArgs().CheckAsync(
            Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UpdateThatChangesThePerson_IsCheckedForConflicts()
    {
        var newPerson = Guid.NewGuid();
        _clientRepository.GetTypeAndDisplayNameAsync(newPerson, Arg.Any<CancellationToken>())
            .Returns(new ClientTypeAndDisplayName(EntityTypeEnum.Employee, "Other"));

        await _guard.EnsureAllowedAsync(
            Change(WorkChangeType.ReplacementWithin, newPerson),
            _parentWork, false, Change(WorkChangeType.ReplacementWithin, _personId), CancellationToken.None);

        await _conflictChecker.Received(1).CheckAsync(
            Arg.Is<IReadOnlyList<PlannedWorkRow>>(rows => rows.Count == 1 && rows[0].ClientId == newPerson),
            null, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UpdateThatWidensTheWindow_IsCheckedForTheAddedTimeOnly()
    {
        var stored = Change(WorkChangeType.ReplacementWithin, _personId);
        var updated = Change(WorkChangeType.ReplacementWithin, _personId);
        updated.EndTime = WorkEnd;

        await _guard.EnsureAllowedAsync(updated, _parentWork, false, stored, CancellationToken.None);

        await _conflictChecker.Received(1).CheckAsync(
            Arg.Is<IReadOnlyList<PlannedWorkRow>>(rows => rows.Count == 1 && rows[0].StartTime == Noon && rows[0].EndTime == WorkEnd),
            Arg.Is<IReadOnlyList<PlannedRemovalRow>>(removals => removals.Count == 0),
            null, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UpdateThatWidensOnBothSides_IsCheckedForBothAddedPieces()
    {
        var stored = Change(WorkChangeType.ReplacementWithin, _personId);
        var updated = Change(WorkChangeType.ReplacementWithin, _personId);
        updated.StartTime = WorkStart;
        updated.EndTime = WorkEnd;

        await _guard.EnsureAllowedAsync(updated, _parentWork, false, stored, CancellationToken.None);

        await _conflictChecker.Received(1).CheckAsync(
            Arg.Is<IReadOnlyList<PlannedWorkRow>>(rows => rows.Count == 2
                && rows[0].StartTime == WorkStart && rows[0].EndTime == Ten
                && rows[1].StartTime == Noon && rows[1].EndTime == WorkEnd),
            Arg.Is<IReadOnlyList<PlannedRemovalRow>>(removals => removals.Count == 0),
            null, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UpdateThatNarrowsTheWindow_IsNotChecked()
    {
        var stored = Change(WorkChangeType.ReplacementWithin, _personId);
        var updated = Change(WorkChangeType.ReplacementWithin, _personId);
        updated.EndTime = Eleven;

        await _guard.EnsureAllowedAsync(updated, _parentWork, false, stored, CancellationToken.None);

        await _conflictChecker.DidNotReceiveWithAnyArgs().CheckAsync(
            Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<IReadOnlyList<PlannedRemovalRow>>(),
            Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UpdateThatShiftsTheWindow_IsCheckedForTheAddedTime_AndTakesTheGivenUpTimeOffTheHours()
    {
        var stored = Change(WorkChangeType.ReplacementWithin, _personId);
        var updated = Change(WorkChangeType.ReplacementWithin, _personId);
        updated.StartTime = Eleven;
        updated.EndTime = TwoPm;

        await _guard.EnsureAllowedAsync(updated, _parentWork, false, stored, CancellationToken.None);

        await _conflictChecker.Received(1).CheckAsync(
            Arg.Is<IReadOnlyList<PlannedWorkRow>>(rows => rows.Count == 1 && rows[0].StartTime == Noon && rows[0].EndTime == TwoPm),
            Arg.Is<IReadOnlyList<PlannedRemovalRow>>(removals => removals.Count == 1
                && removals[0].StartTime == Ten && removals[0].EndTime == Eleven
                && removals[0].WorkId == stored.Id && removals[0].ClientId == _personId),
            null, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UpdateToADisjointWindow_IsCheckedInFull_AndTakesTheStoredWindowOffTheHours()
    {
        var stored = Change(WorkChangeType.ReplacementWithin, _personId);
        var updated = Change(WorkChangeType.ReplacementWithin, _personId);
        updated.StartTime = TwoPm;
        updated.EndTime = ThreePm;

        await _guard.EnsureAllowedAsync(updated, _parentWork, false, stored, CancellationToken.None);

        await _conflictChecker.Received(1).CheckAsync(
            Arg.Is<IReadOnlyList<PlannedWorkRow>>(rows => rows.Count == 1 && rows[0].StartTime == TwoPm && rows[0].EndTime == ThreePm),
            Arg.Is<IReadOnlyList<PlannedRemovalRow>>(removals => removals.Count == 1
                && removals[0].StartTime == Ten && removals[0].EndTime == Noon),
            null, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UpdateThatMovesTheChangeToAnotherWork_IsCheckedForConflicts()
    {
        var stored = Change(WorkChangeType.ReplacementWithin, _personId);
        stored.WorkId = Guid.NewGuid();

        await _guard.EnsureAllowedAsync(
            Change(WorkChangeType.ReplacementWithin, _personId), _parentWork, false, stored, CancellationToken.None);

        await _conflictChecker.ReceivedWithAnyArgs(1).CheckAsync(
            Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UpdateFromACorrectionToAReplacement_IsCheckedForConflicts()
    {
        var stored = Change(WorkChangeType.CorrectionEnd, null);

        await _guard.EnsureAllowedAsync(
            Change(WorkChangeType.ReplacementWithin, _personId), _parentWork, false, stored, CancellationToken.None);

        await _conflictChecker.ReceivedWithAnyArgs(1).CheckAsync(
            Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UpdateFromAReplacementToACorrection_NeedsNoPerson()
    {
        var stored = Change(WorkChangeType.ReplacementWithin, _personId);

        await _guard.EnsureAllowedAsync(
            Change(WorkChangeType.CorrectionEnd, null), _parentWork, false, stored, CancellationToken.None);

        await _conflictChecker.DidNotReceiveWithAnyArgs().CheckAsync(
            Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task BlockingConflictWithoutOverride_IsRefused()
    {
        GivenConflict(EscalatedError());

        var exception = await Should.ThrowAsync<ConflictException>(() => _guard.EnsureAllowedAsync(
            Change(WorkChangeType.ReplacementWithin, _personId), _parentWork, false, null, CancellationToken.None));

        exception.Message.ShouldContain("blocked");
    }

    [Test]
    public async Task EscalatedConflictWithAuthorizedOverride_IsAllowed()
    {
        GivenConflict(EscalatedError());
        _overrideAuthorizer.IsAuthorizedAsync(true).Returns(true);

        await _guard.EnsureAllowedAsync(
            Change(WorkChangeType.ReplacementWithin, _personId), _parentWork, true, null, CancellationToken.None);
    }

    [Test]
    public async Task HardBlockingConflict_IsRefusedEvenWithAuthorizedOverride()
    {
        GivenConflict(HardBlockingError());
        _overrideAuthorizer.IsAuthorizedAsync(true).Returns(true);

        await Should.ThrowAsync<ConflictException>(() => _guard.EnsureAllowedAsync(
            Change(WorkChangeType.ReplacementWithin, _personId), _parentWork, true, null, CancellationToken.None));
    }

    private WorkChange Change(WorkChangeType type, Guid? personId, Guid? analyseToken = null) => new()
    {
        Id = Guid.NewGuid(),
        WorkId = _parentWork.Id,
        Type = type,
        ReplaceClientId = personId,
        StartTime = Ten,
        EndTime = Noon,
        ChangeTime = HalfShiftHours,
        AnalyseToken = analyseToken,
    };

    private void GivenConflict(params ScheduleValidationNotificationDto[] conflicts)
    {
        _conflictChecker.CheckAsync(
                Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new PreCommitCheckResult(conflicts));
    }

    private ScheduleValidationNotificationDto HardBlockingError() => new()
    {
        Type = ScheduleValidationType.Error,
        ClientId = _personId,
        Date = Day,
        Comment = "schedule.error-list.missing-mandatory-qualification",
    };

    private ScheduleValidationNotificationDto EscalatedError() => new()
    {
        Type = ScheduleValidationType.Error,
        ClientId = _personId,
        Date = Day,
        Comment = "schedule.error-list.rest-violation",
        CommentParams = new Dictionary<string, string>
        {
            [ComplianceRuleNames.EnforcementRuleParamKey] = ComplianceRuleNames.MinRestHours,
        },
    };
}
