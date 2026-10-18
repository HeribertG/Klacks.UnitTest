// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.DTOs.Notifications;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Services.Shifts;

namespace Klacks.UnitTest.Application.Services.Schedules;

/// <summary>
/// WorkBookingDiagnosisService: one case per refusal reason with its code, channels and remedy; the direct-entry order of
/// the Works POST (seal, period, sporadic, hard-blocking); a hidden or missing employee, shift or scenario ends the
/// diagnosis as NotFound before any further dependency is asked; a scenario skips the seal and the hard-blocking check
/// of the direct entry but not of the assistant; an occupying work on a hidden shift is reported without shift name or
/// lock level; works of another scenario and container sub-breaks do not occupy the cell.
/// </summary>
[TestFixture]
public class WorkBookingDiagnosisServiceTests
{
    private const string ShiftName = "Frühdienst";
    private const string HiddenShiftName = "Geheimdienst";
    private const string HardBlockingComment = "test.mandatory-qualification";
    private const string WarningComment = "test.rest-time";

    private static readonly DateOnly Day = new(2026, 11, 2);
    private static readonly TimeOnly Start = new(7, 0);
    private static readonly TimeOnly End = new(15, 0);

    private IScheduleRowAccessGuard _rowAccess = null!;
    private IClientRepository _clients = null!;
    private IShiftVisibilityGuard _shiftGuard = null!;
    private IShiftRepository _shifts = null!;
    private ISealedDayRepository _sealedDays = null!;
    private IClientPlanningPeriodGuard _periodGuard = null!;
    private IWorkWriteGuard _writeGuard = null!;
    private IPreCommitConflictChecker _checker = null!;
    private IWorkRepository _works = null!;
    private IBreakRepository _breaks = null!;

    private Guid _clientId;
    private Guid _shiftId;
    private Guid _hiddenShiftId;

    [SetUp]
    public void SetUp()
    {
        _clientId = Guid.NewGuid();
        _shiftId = Guid.NewGuid();
        _hiddenShiftId = Guid.NewGuid();

        _rowAccess = Substitute.For<IScheduleRowAccessGuard>();
        _rowAccess.IsVisibleAsync(_clientId, Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(true);
        _clients = Substitute.For<IClientRepository>();
        _clients.Get(_clientId).Returns(new Client { Id = _clientId, FirstName = "Anna", Name = "Muster" });
        _shiftGuard = Substitute.For<IShiftVisibilityGuard>();
        _shiftGuard.ExistsAndVisibleAsync(_shiftId, Arg.Any<CancellationToken>()).Returns(true);
        _shiftGuard.FilterVisibleAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call => (IReadOnlySet<Guid>)call.Arg<IReadOnlyCollection<Guid>>().Where(id => id != _hiddenShiftId).ToHashSet());
        _shifts = Substitute.For<IShiftRepository>();
        _shifts.Get(_shiftId).Returns(new Shift { Id = _shiftId, Name = ShiftName, StartShift = Start, EndShift = End });
        _shifts.GetSporadicInfoAsync(_shiftId, Arg.Any<CancellationToken>())
            .Returns(new SporadicShiftInfo(ShiftName, false, ShiftSporadic.Week, Day, null, 1, 1));
        _shifts.GetSporadicInfoAsync(_hiddenShiftId, Arg.Any<CancellationToken>())
            .Returns(new SporadicShiftInfo(HiddenShiftName, false, ShiftSporadic.Week, Day, null, 1, 1));
        _sealedDays = Substitute.For<ISealedDayRepository>();
        _sealedDays.GetLockedPairsAsync(
                Arg.Any<IReadOnlyCollection<(DateOnly, Guid)>>(), Arg.Any<WorkLockLevel>(), Arg.Any<CancellationToken>())
            .Returns(new HashSet<(DateOnly, Guid)>());
        _periodGuard = Substitute.For<IClientPlanningPeriodGuard>();
        _periodGuard.FindViolationsAsync(Arg.Any<IReadOnlyCollection<(Guid, DateOnly)>>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PlanningPeriodViolation>());
        _writeGuard = Substitute.For<IWorkWriteGuard>();
        _checker = Substitute.For<IPreCommitConflictChecker>();
        _checker.CheckAsync(Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(PreCommitCheckResult.Empty);
        _works = Substitute.For<IWorkRepository>();
        _works.GetByClientAndDateRangeAsync(_clientId, Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(new List<Work>());
        _breaks = Substitute.For<IBreakRepository>();
        _breaks.GetByClientAndDateRangeAsync(_clientId, Day, Day, Arg.Any<CancellationToken>()).Returns(new List<Break>());
    }

    [Test]
    public async Task NothingRefuses_IsBookable_WithNamesAndNoFinding()
    {
        var diagnosis = await DiagnoseAsync();

        diagnosis.Outcome.ShouldBe(WorkBookingDiagnosisOutcome.Bookable);
        diagnosis.ClientName.ShouldBe("Anna Muster");
        diagnosis.ShiftName.ShouldBe(ShiftName);
        diagnosis.Refusals.ShouldBeEmpty();
        diagnosis.DirectEntryCode.ShouldBeNull();
    }

    [Test]
    public async Task HiddenEmployee_IsNotFound_BeforeAnyOtherDependencyIsAsked()
    {
        _rowAccess.IsVisibleAsync(_clientId, Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(false);

        var diagnosis = await DiagnoseAsync();

        diagnosis.ShouldBeSameAs(WorkBookingDiagnosis.NotFound);
        diagnosis.ClientName.ShouldBeNull();
        diagnosis.Refusals.ShouldBeEmpty();
        await _clients.DidNotReceiveWithAnyArgs().Get(default);
        await _shiftGuard.DidNotReceiveWithAnyArgs().ExistsAndVisibleAsync(default, default);
        await AssertNoCheckRanAsync();
    }

    [Test]
    public async Task HiddenShift_IsNotFound_BeforeAnySealPeriodOrConflictCheck()
    {
        _shiftGuard.ExistsAndVisibleAsync(_shiftId, Arg.Any<CancellationToken>()).Returns(false);
        _sealedDays.IsDayLockedAsync(Day, _clientId, Arg.Any<CancellationToken>()).Returns(true);

        var diagnosis = await DiagnoseAsync();

        diagnosis.ShouldBeSameAs(WorkBookingDiagnosis.NotFound);
        await _shifts.DidNotReceiveWithAnyArgs().Get(default);
        await AssertNoCheckRanAsync();
    }

    [Test]
    public async Task MissingEmployee_IsNotFound_LikeAHiddenOne()
    {
        _clients.Get(_clientId).Returns((Client?)null);

        var diagnosis = await DiagnoseAsync();

        diagnosis.ShouldBeSameAs(WorkBookingDiagnosis.NotFound);
        await AssertNoCheckRanAsync();
    }

    [Test]
    public async Task HiddenScenario_IsNotFound()
    {
        var token = Guid.NewGuid();
        _rowAccess.IsVisibleAsync(_clientId, token, Arg.Any<CancellationToken>()).Returns(false);

        var diagnosis = await DiagnoseAsync(token);

        diagnosis.ShouldBeSameAs(WorkBookingDiagnosis.NotFound);
        await AssertNoCheckRanAsync();
    }

    [Test]
    public async Task ClosedPeriod_RefusesDirectEntryAndAssistant_WithTheAdminReopenRemedy()
    {
        GivenSealed(WorkLockLevel.Closed);

        var refusal = Single(await DiagnoseAsync());

        refusal.Reason.ShouldBe(WorkBookingRefusalReason.PeriodClosed);
        refusal.Code.ShouldBe(WorkBookingRefusalCodes.DaySealed);
        refusal.Channels.ShouldBe(new WorkBookingChannels(DirectEntry: true, Assistant: true, Grid: false));
        refusal.Remedy.ShouldBe(WorkBookingRemedy.ReopenPeriodAdmin);
        refusal.Details[WorkBookingRefusalCodes.SealLevelField].ShouldBe(WorkBookingSealKind.PeriodClosed);
    }

    [Test]
    public async Task DayApproval_IsNamedAsSuch_WithTheRevokeRemedy()
    {
        GivenSealed(lockingLevel: null);

        var refusal = Single(await DiagnoseAsync());

        refusal.Reason.ShouldBe(WorkBookingRefusalReason.DayApproved);
        refusal.Code.ShouldBe(WorkBookingRefusalCodes.DaySealed);
        refusal.Details[WorkBookingRefusalCodes.SealLevelField].ShouldBe(WorkBookingSealKind.DayApproval);
        refusal.Remedy.ShouldBe(WorkBookingRemedy.RevokeDayApproval);
    }

    [Test]
    public async Task InAScenario_TheSealRefusesOnlyTheAssistant_AndTheScenarioWorkGoesToTheSharedGuard()
    {
        GivenSealed(WorkLockLevel.Closed);
        var token = Guid.NewGuid();

        var diagnosis = await DiagnoseAsync(token);

        Single(diagnosis).Channels.ShouldBe(new WorkBookingChannels(DirectEntry: false, Assistant: true, Grid: false));
        diagnosis.DirectEntryCode.ShouldBeNull();
        await _writeGuard.Received(1).FindHardBlockingRefusalAsync(
            Arg.Is<Work>(w => w.AnalyseToken == token), Arg.Any<CancellationToken>());
        await _writeGuard.DidNotReceiveWithAnyArgs()
            .FindHardBlockingRefusalAsync(default(IReadOnlyList<PlannedWorkRow>)!, default, default, default);
        await _checker.Received(1).CheckAsync(Arg.Any<IReadOnlyList<PlannedWorkRow>>(), token, Arg.Any<CancellationToken>());
    }

    [TestCase(WorkBookingRefusalCodes.OutsideMembershipPeriod, WorkBookingRefusalReason.OutsideMembershipPeriod, WorkBookingRemedy.AdjustMembership)]
    [TestCase(WorkBookingRefusalCodes.OutsideGroupPeriod, WorkBookingRefusalReason.OutsideGroupPeriod, WorkBookingRemedy.AdjustGroupMembership)]
    public async Task OutsideAPlanningPeriod_RefusesEveryChannel_WithTheBounds(
        string code, WorkBookingRefusalReason reason, WorkBookingRemedy remedy)
    {
        var from = Day.AddDays(-30);
        var until = Day.AddDays(-1);
        _periodGuard.FindViolationsAsync(Arg.Any<IReadOnlyCollection<(Guid, DateOnly)>>(), Arg.Any<CancellationToken>())
            .Returns([new PlanningPeriodViolation(_clientId, Day, "text", code, from, until)]);

        var refusal = Single(await DiagnoseAsync());

        refusal.Reason.ShouldBe(reason);
        refusal.Code.ShouldBe(code);
        refusal.Remedy.ShouldBe(remedy);
        refusal.Channels.ShouldBe(new WorkBookingChannels(DirectEntry: true, Assistant: true, Grid: true));
        refusal.Details[WorkBookingRefusalCodes.FromField].ShouldBe(from);
        refusal.Details[WorkBookingRefusalCodes.UntilField].ShouldBe(until);
    }

    [TestCase(WorkWriteConflictCodes.SporadicShiftDayFull, WorkBookingRefusalReason.SporadicShiftDayFull)]
    [TestCase(WorkWriteConflictCodes.SporadicShiftRangeExhausted, WorkBookingRefusalReason.SporadicShiftRangeExhausted)]
    public async Task SporadicCapacity_RefusesOnlyTheDirectEntryAndTheGrid_NotTheAssistant(
        string code, WorkBookingRefusalReason reason)
    {
        var details = new Dictionary<string, object?> { [WorkWriteConflictCodes.CapacityField] = 2 };
        _writeGuard.FindSporadicRefusalAsync(Arg.Any<Work>(), Arg.Any<CancellationToken>())
            .Returns(new WorkBookingRefusal(code, "text", details));

        var refusal = Single(await DiagnoseAsync());

        refusal.Reason.ShouldBe(reason);
        refusal.Code.ShouldBe(code);
        refusal.Channels.ShouldBe(new WorkBookingChannels(DirectEntry: true, Assistant: false, Grid: true));
        refusal.Details.ShouldBeSameAs(details);
        await _writeGuard.Received(1).FindSporadicRefusalAsync(
            Arg.Is<Work>(w => w.ClientId == _clientId && w.ShiftId == _shiftId && w.CurrentDate == Day
                              && w.Id == Guid.Empty && w.StartTime == Start && w.EndTime == End),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task HardBlockingConflict_UsesTheSharedGuard_AndRefusesDirectEntryAndAssistant()
    {
        var item = new WorkConflictItem(HardBlockingComment, _clientId, Day, new Dictionary<string, string>());
        _writeGuard.FindHardBlockingRefusalAsync(Arg.Any<Work>(), Arg.Any<CancellationToken>())
            .Returns(new WorkBookingRefusal(
                WorkWriteConflictCodes.BlockedByConflicts, "text",
                new Dictionary<string, object?> { [WorkWriteConflictCodes.ConflictsField] = new List<WorkConflictItem> { item } }));
        GivenCheck(Conflict(ScheduleValidationType.Error, HardBlockingComment));

        var refusal = Single(await DiagnoseAsync());

        refusal.Reason.ShouldBe(WorkBookingRefusalReason.HardBlockingConflict);
        refusal.Code.ShouldBe(WorkWriteConflictCodes.BlockedByConflicts);
        refusal.Channels.ShouldBe(new WorkBookingChannels(DirectEntry: true, Assistant: true, Grid: false));
        refusal.Remedy.ShouldBe(WorkBookingRemedy.AddQualificationOrRelaxLevel);
        refusal.Conflicts.ShouldBe([item]);
        await _writeGuard.Received(1).FindHardBlockingRefusalAsync(
            Arg.Is<Work>(w => w.ClientId == _clientId && w.CurrentDate == Day && w.ShiftId == _shiftId
                              && w.StartTime == Start && w.EndTime == End && w.AnalyseToken == null),
            Arg.Any<CancellationToken>());
        await _writeGuard.DidNotReceiveWithAnyArgs()
            .FindHardBlockingRefusalAsync(default(IReadOnlyList<PlannedWorkRow>)!, default, default, default);
    }

    [Test]
    public async Task Collision_RefusesOnlyTheAssistant_TheDirectEntryWouldSave()
    {
        GivenCheck(Conflict(ScheduleValidationType.Error, ScheduleValidationKeys.Collision));

        var diagnosis = await DiagnoseAsync();
        var refusal = Single(diagnosis);

        refusal.Reason.ShouldBe(WorkBookingRefusalReason.AssistantOnlyConflict);
        refusal.Code.ShouldBeNull();
        refusal.Channels.ShouldBe(new WorkBookingChannels(DirectEntry: false, Assistant: true, Grid: false));
        refusal.Conflicts.Single().Code.ShouldBe(ScheduleValidationKeys.Collision);
        diagnosis.Outcome.ShouldBe(WorkBookingDiagnosisOutcome.Refused);
        diagnosis.DirectEntryCode.ShouldBeNull();
    }

    [Test]
    public async Task Warning_IsAnAdvisory_AndRefusesNothing()
    {
        GivenCheck(Conflict(ScheduleValidationType.Warning, WarningComment));

        var diagnosis = await DiagnoseAsync();

        diagnosis.Outcome.ShouldBe(WorkBookingDiagnosisOutcome.Bookable);
        diagnosis.Advisories.Single().Comment.ShouldBe(WarningComment);
    }

    [Test]
    public async Task OccupyingWorkOnAHiddenShift_IsReportedWithoutShiftNameOrLockLevel()
    {
        GivenWorks(new Work { ShiftId = _hiddenShiftId, ClientId = _clientId, CurrentDate = Day, LockLevel = WorkLockLevel.Confirmed });

        var diagnosis = await DiagnoseAsync();
        var refusal = Single(diagnosis);

        diagnosis.Outcome.ShouldBe(WorkBookingDiagnosisOutcome.GridOnly);
        refusal.Reason.ShouldBe(WorkBookingRefusalReason.CellOccupiedByWork);
        refusal.Channels.ShouldBe(new WorkBookingChannels(DirectEntry: false, Assistant: false, Grid: true));
        refusal.Remedy.ShouldBe(WorkBookingRemedy.AskForGroupVisibility);
        refusal.Details.ShouldNotContainKey(WorkWriteConflictCodes.ShiftNameField);
        refusal.Details.ShouldNotContainKey(WorkBookingRefusalCodes.LockLevelField);
        refusal.Details.Values.ShouldNotContain(HiddenShiftName);
        await _shifts.DidNotReceive().GetSporadicInfoAsync(_hiddenShiftId, Arg.Any<CancellationToken>());
    }

    [TestCase(WorkLockLevel.None, WorkBookingRemedy.RemoveOrMoveExistingEntry)]
    [TestCase(WorkLockLevel.Confirmed, WorkBookingRemedy.RemoveConfirmationFirst)]
    [TestCase(WorkLockLevel.Approved, WorkBookingRemedy.RevokeDayApproval)]
    [TestCase(WorkLockLevel.Closed, WorkBookingRemedy.ReopenPeriodAdmin)]
    public async Task OccupyingVisibleWork_IsNamed_WithTheRemedyOfItsLock(WorkLockLevel lockLevel, WorkBookingRemedy remedy)
    {
        GivenWorks(new Work { ShiftId = _shiftId, ClientId = _clientId, CurrentDate = Day, LockLevel = lockLevel });

        var refusal = Single(await DiagnoseAsync());

        refusal.Details[WorkWriteConflictCodes.ShiftNameField].ShouldBe(ShiftName);
        refusal.Details[WorkBookingRefusalCodes.LockLevelField].ShouldBe(lockLevel);
        refusal.Remedy.ShouldBe(remedy);
    }

    [Test]
    public async Task WorksOfAnotherScenario_AndContainerChildren_DoNotOccupyTheCell()
    {
        GivenWorks(
            new Work { ShiftId = _shiftId, ClientId = _clientId, CurrentDate = Day, AnalyseToken = Guid.NewGuid() },
            new Work { ShiftId = _shiftId, ClientId = _clientId, CurrentDate = Day, ParentWorkId = Guid.NewGuid() });

        (await DiagnoseAsync()).Outcome.ShouldBe(WorkBookingDiagnosisOutcome.Bookable);
    }

    [Test]
    public async Task Absence_OccupiesTheCell_ButAContainerSubBreakDoesNot()
    {
        _breaks.GetByClientAndDateRangeAsync(_clientId, Day, Day, Arg.Any<CancellationToken>())
            .Returns(new List<Break> { new() { ClientId = _clientId, CurrentDate = Day } });

        var refusal = Single(await DiagnoseAsync());
        refusal.Reason.ShouldBe(WorkBookingRefusalReason.CellOccupiedByAbsence);

        _breaks.GetByClientAndDateRangeAsync(_clientId, Day, Day, Arg.Any<CancellationToken>())
            .Returns(new List<Break> { new() { ClientId = _clientId, CurrentDate = Day, ParentWorkId = Guid.NewGuid() } });

        (await DiagnoseAsync()).Refusals.ShouldBeEmpty();
    }

    [Test]
    public async Task SeveralReasons_AreListedInTheDirectEntryOrder_AndTheFirstGivesTheCode()
    {
        GivenSealed(WorkLockLevel.Closed);
        _periodGuard.FindViolationsAsync(Arg.Any<IReadOnlyCollection<(Guid, DateOnly)>>(), Arg.Any<CancellationToken>())
            .Returns([new PlanningPeriodViolation(_clientId, Day, "text", WorkBookingRefusalCodes.OutsideMembershipPeriod, null, null)]);
        _writeGuard.FindSporadicRefusalAsync(Arg.Any<Work>(), Arg.Any<CancellationToken>())
            .Returns(new WorkBookingRefusal(WorkWriteConflictCodes.SporadicShiftDayFull, "text", new Dictionary<string, object?>()));

        var diagnosis = await DiagnoseAsync();

        diagnosis.Refusals.Select(r => r.Reason).ShouldBe(
        [
            WorkBookingRefusalReason.PeriodClosed,
            WorkBookingRefusalReason.OutsideMembershipPeriod,
            WorkBookingRefusalReason.SporadicShiftDayFull,
        ]);
        diagnosis.DirectEntryCode.ShouldBe(WorkBookingRefusalCodes.DaySealed);
    }

    private Task<WorkBookingDiagnosis> DiagnoseAsync(Guid? analyseToken = null) =>
        new WorkBookingDiagnosisService(
                _rowAccess, _clients, _shiftGuard, _shifts, _sealedDays, _periodGuard, _writeGuard, _checker, _works, _breaks)
            .DiagnoseAsync(new WorkBookingDiagnosisRequest(_clientId, _shiftId, Day, analyseToken), CancellationToken.None);

    private static WorkBookingRefusalFinding Single(WorkBookingDiagnosis diagnosis) => diagnosis.Refusals.ShouldHaveSingleItem();

    private void GivenSealed(WorkLockLevel? lockingLevel)
    {
        _sealedDays.IsDayLockedAsync(Day, _clientId, Arg.Any<CancellationToken>()).Returns(true);
        var pair = new HashSet<(DateOnly, Guid)> { (Day, _clientId) };
        _sealedDays.GetLockedPairsAsync(
                Arg.Any<IReadOnlyCollection<(DateOnly, Guid)>>(), WorkLockLevel.Closed, Arg.Any<CancellationToken>())
            .Returns(lockingLevel == WorkLockLevel.Closed ? pair : new HashSet<(DateOnly, Guid)>());
    }

    private void GivenCheck(params ScheduleValidationNotificationDto[] conflicts) =>
        _checker.CheckAsync(Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new PreCommitCheckResult(conflicts));

    private void GivenWorks(params Work[] works) =>
        _works.GetByClientAndDateRangeAsync(_clientId, Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(works.ToList());

    private ScheduleValidationNotificationDto Conflict(ScheduleValidationType type, string comment) => new()
    {
        Type = type,
        ClientId = _clientId,
        Date = Day,
        Comment = comment,
    };

    private async Task AssertNoCheckRanAsync()
    {
        await _sealedDays.DidNotReceiveWithAnyArgs().IsDayLockedAsync(default, default, default);
        await _periodGuard.DidNotReceiveWithAnyArgs().FindViolationsAsync(default!, default);
        await _writeGuard.DidNotReceiveWithAnyArgs().FindSporadicRefusalAsync(default!, default);
        await _writeGuard.DidNotReceiveWithAnyArgs().FindHardBlockingRefusalAsync(default(Work)!, default);
        await _writeGuard.DidNotReceiveWithAnyArgs()
            .FindHardBlockingRefusalAsync(default(IReadOnlyList<PlannedWorkRow>)!, default, default, default);
        await _checker.DidNotReceiveWithAnyArgs().CheckAsync(default!, default, default);
        await _works.DidNotReceiveWithAnyArgs().GetByClientAndDateRangeAsync(default, default, default, default);
        await _breaks.DidNotReceiveWithAnyArgs().GetByClientAndDateRangeAsync(default, default, default, default);
    }
}
