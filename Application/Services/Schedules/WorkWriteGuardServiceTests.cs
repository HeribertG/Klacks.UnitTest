// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for WorkWriteGuardService, the pre-write checks shared by the Work create and restore
/// handlers: sporadic-shift capacity (day and range) and the hard-blocking conflict class that can never
/// be reported after the fact. Scenario writes bypass the hard-blocking check like the day-lock guard.
/// </summary>

using Klacks.Api.Application.Constants;
using Klacks.Api.Application.DTOs.Notifications;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Services.Shifts;

namespace Klacks.UnitTest.Application.Services.Schedules;

[TestFixture]
public class WorkWriteGuardServiceTests
{
    private const string ShiftName = "Night watch";

    private IShiftRepository _shiftRepository = null!;
    private IWorkRepository _workRepository = null!;
    private IPreCommitConflictChecker _conflictChecker = null!;
    private WorkWriteGuardService _sut = null!;

    private readonly Guid _shiftId = Guid.NewGuid();
    private readonly Guid _clientId = Guid.NewGuid();
    private readonly DateOnly _date = new(2027, 3, 10);

    [SetUp]
    public void Setup()
    {
        _shiftRepository = Substitute.For<IShiftRepository>();
        _workRepository = Substitute.For<IWorkRepository>();
        _conflictChecker = Substitute.For<IPreCommitConflictChecker>();
        _conflictChecker.CheckAsync(Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(PreCommitCheckResult.Empty);
        _sut = new WorkWriteGuardService(_shiftRepository, _workRepository, _conflictChecker);
    }

    [Test]
    public async Task EnsureNoSporadicConflictAsync_NonSporadicShift_NeverQueriesCapacity()
    {
        _shiftRepository.GetSporadicInfoAsync(_shiftId, Arg.Any<CancellationToken>())
            .Returns(SporadicInfo(isSporadic: false, sumEmployees: 1, quantity: 1));

        await _sut.EnsureNoSporadicConflictAsync(NewWork(), CancellationToken.None);

        await _workRepository.DidNotReceiveWithAnyArgs().GetSporadicCapacityUsageAsync(
            default, default, default, default, default, default, default);
    }

    [Test]
    public async Task EnsureNoSporadicConflictAsync_DayFullyBooked_Throws()
    {
        GivenSporadicUsage(engagedAtDay: 2, distinctBookedDays: 1, sumEmployees: 2, quantity: 5);

        var act = async () => await _sut.EnsureNoSporadicConflictAsync(NewWork(), CancellationToken.None);

        var exception = await act.ShouldThrowAsync<ConflictException>();
        exception.Message.ShouldContain("fully booked");
    }

    [Test]
    public async Task EnsureNoSporadicConflictAsync_DayFullyBooked_CarriesCodeAndDetailsForTheClient()
    {
        GivenSporadicUsage(engagedAtDay: 2, distinctBookedDays: 1, sumEmployees: 2, quantity: 5);

        var act = async () => await _sut.EnsureNoSporadicConflictAsync(NewWork(), CancellationToken.None);

        var exception = await act.ShouldThrowAsync<WorkWriteConflictException>();
        exception.ErrorCode.ShouldBe(WorkWriteConflictCodes.SporadicShiftDayFull);
        exception.Details[WorkWriteConflictCodes.ShiftIdField].ShouldBe(_shiftId);
        exception.Details[WorkWriteConflictCodes.ShiftNameField].ShouldBe(ShiftName);
        exception.Details[WorkWriteConflictCodes.DateField].ShouldBe(_date);
        exception.Details[WorkWriteConflictCodes.EngagedField].ShouldBe(2);
        exception.Details[WorkWriteConflictCodes.CapacityField].ShouldBe(2);
    }

    [Test]
    public async Task EnsureNoSporadicConflictAsync_RangeQuantityExhausted_CarriesCodeAndRange()
    {
        GivenSporadicUsage(engagedAtDay: 0, distinctBookedDays: 5, sumEmployees: 2, quantity: 5);

        var act = async () => await _sut.EnsureNoSporadicConflictAsync(NewWork(), CancellationToken.None);

        var exception = await act.ShouldThrowAsync<WorkWriteConflictException>();
        exception.ErrorCode.ShouldBe(WorkWriteConflictCodes.SporadicShiftRangeExhausted);
        exception.Details[WorkWriteConflictCodes.BookedField].ShouldBe(5);
        exception.Details[WorkWriteConflictCodes.CapacityField].ShouldBe(5);
        exception.Details.ContainsKey(WorkWriteConflictCodes.RangeFromField).ShouldBeTrue();
        exception.Details.ContainsKey(WorkWriteConflictCodes.RangeUntilField).ShouldBeTrue();
    }

    [Test]
    public async Task EnsureNoSporadicConflictAsync_RangeQuantityExhaustedOnNewDay_Throws()
    {
        GivenSporadicUsage(engagedAtDay: 0, distinctBookedDays: 5, sumEmployees: 2, quantity: 5);

        var act = async () => await _sut.EnsureNoSporadicConflictAsync(NewWork(), CancellationToken.None);

        var exception = await act.ShouldThrowAsync<ConflictException>();
        exception.Message.ShouldContain("range capacity");
    }

    [Test]
    public async Task EnsureNoSporadicConflictAsync_CapacityLeft_Passes()
    {
        GivenSporadicUsage(engagedAtDay: 1, distinctBookedDays: 5, sumEmployees: 2, quantity: 5);

        await _sut.EnsureNoSporadicConflictAsync(NewWork(), CancellationToken.None);
    }

    [Test]
    public async Task EnsureNoSporadicConflictAsync_ExcludesTheWorkItself_WhenItHasAnId()
    {
        var work = NewWork();
        work.Id = Guid.NewGuid();
        GivenSporadicUsage(engagedAtDay: 0, distinctBookedDays: 0, sumEmployees: 1, quantity: 1);

        await _sut.EnsureNoSporadicConflictAsync(work, CancellationToken.None);

        await _workRepository.Received(1).GetSporadicCapacityUsageAsync(
            _shiftId, _date, Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), work.Id, null, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task EnsureNoHardBlockingConflictAsync_HardBlocking_Throws()
    {
        _conflictChecker.CheckAsync(Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new PreCommitCheckResult([HardBlocking()]));

        var act = async () => await _sut.EnsureNoHardBlockingConflictAsync(NewWork(), CancellationToken.None);

        await act.ShouldThrowAsync<ConflictException>();
    }

    [Test]
    public async Task EnsureNoHardBlockingConflictAsync_HardBlocking_CarriesTheConflictItemsAndTheShiftName()
    {
        _shiftRepository.GetSporadicInfoAsync(_shiftId, Arg.Any<CancellationToken>())
            .Returns(SporadicInfo(isSporadic: false, sumEmployees: 1, quantity: 1));
        _conflictChecker.CheckAsync(Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new PreCommitCheckResult([Collision(), HardBlocking()]));

        var act = async () => await _sut.EnsureNoHardBlockingConflictAsync(NewWork(), CancellationToken.None);

        var exception = await act.ShouldThrowAsync<WorkWriteConflictException>();
        exception.ErrorCode.ShouldBe(WorkWriteConflictCodes.BlockedByConflicts);
        exception.Details[WorkWriteConflictCodes.ShiftIdField].ShouldBe(_shiftId);
        exception.Details[WorkWriteConflictCodes.ShiftNameField].ShouldBe(ShiftName);
        var items = exception.Details[WorkWriteConflictCodes.ConflictsField].ShouldBeOfType<List<WorkConflictItem>>();
        items.Count.ShouldBe(1);
        items[0].Code.ShouldBe("qualification-missing");
        items[0].ClientId.ShouldBe(_clientId);
        items[0].Date.ShouldBe(_date);
    }

    [Test]
    public async Task EnsureNoHardBlockingConflictAsync_HardBlockingWithUnknownShift_StillThrowsWithoutShiftName()
    {
        _conflictChecker.CheckAsync(Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new PreCommitCheckResult([HardBlocking()]));

        var act = async () => await _sut.EnsureNoHardBlockingConflictAsync(NewWork(), CancellationToken.None);

        var exception = await act.ShouldThrowAsync<WorkWriteConflictException>();
        exception.Details[WorkWriteConflictCodes.ShiftNameField].ShouldBeNull();
    }

    [Test]
    public async Task EnsureNoHardBlockingConflictAsync_CollisionOnly_Passes()
    {
        _conflictChecker.CheckAsync(Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new PreCommitCheckResult([Collision()]));

        await _sut.EnsureNoHardBlockingConflictAsync(NewWork(), CancellationToken.None);
    }

    [Test]
    public async Task EnsureNoHardBlockingConflictAsync_ScenarioWrite_SkipsTheCheck()
    {
        var work = NewWork();
        work.AnalyseToken = Guid.NewGuid();

        await _sut.EnsureNoHardBlockingConflictAsync(work, CancellationToken.None);

        await _conflictChecker.DidNotReceiveWithAnyArgs().CheckAsync(default!, default(Guid?), default);
    }

    [Test]
    public async Task EnsureNoHardBlockingConflictAsync_ChecksExactlyThePlannedRow()
    {
        var work = NewWork();

        await _sut.EnsureNoHardBlockingConflictAsync(work, CancellationToken.None);

        await _conflictChecker.Received(1).CheckAsync(
            Arg.Is<IReadOnlyList<PlannedWorkRow>>(rows =>
                rows.Count == 1
                && rows[0].ClientId == _clientId
                && rows[0].Date == _date
                && rows[0].StartTime == work.StartTime
                && rows[0].EndTime == work.EndTime
                && rows[0].ShiftId == _shiftId),
            null,
            Arg.Any<CancellationToken>());
    }

    [TestCase(2, 1, 2, 5)]
    [TestCase(0, 5, 2, 5)]
    [TestCase(1, 5, 2, 5)]
    [TestCase(0, 0, 1, 1)]
    public async Task SporadicCheck_EnsureThrowsExactlyWhenFindReportsARefusal_WithTheSameCodeAndDetails(
        int engagedAtDay, int distinctBookedDays, int sumEmployees, int quantity)
    {
        GivenSporadicUsage(engagedAtDay, distinctBookedDays, sumEmployees, quantity);

        var refusal = await _sut.FindSporadicRefusalAsync(NewWork(), CancellationToken.None);
        var thrown = await CatchAsync(() => _sut.EnsureNoSporadicConflictAsync(NewWork(), CancellationToken.None));

        AssertCoupled(refusal, thrown);
    }

    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(true, true)]
    public async Task HardBlockingCheck_EnsureThrowsExactlyWhenFindReportsARefusal_WithTheSameCodeAndDetails(
        bool withCollision, bool withHardBlocking)
    {
        var conflicts = new List<ScheduleValidationNotificationDto>();
        if (withCollision)
        {
            conflicts.Add(Collision());
        }

        if (withHardBlocking)
        {
            conflicts.Add(HardBlocking());
        }

        _shiftRepository.GetSporadicInfoAsync(_shiftId, Arg.Any<CancellationToken>())
            .Returns(SporadicInfo(isSporadic: false, sumEmployees: 1, quantity: 1));
        _conflictChecker.CheckAsync(Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new PreCommitCheckResult(conflicts));
        var work = NewWork();

        var refusal = await _sut.FindHardBlockingRefusalAsync(work, CancellationToken.None);
        var rowRefusal = await _sut.FindHardBlockingRefusalAsync(
            [PlannedWorkRow.From(work)], null, WorkWriteOperation.Create, CancellationToken.None);
        var thrown = await CatchAsync(() => _sut.EnsureNoHardBlockingConflictAsync(work, CancellationToken.None));

        AssertCoupled(refusal, thrown);
        AssertCoupled(rowRefusal, thrown);
        (refusal != null).ShouldBe(withHardBlocking);
    }

    [Test]
    public async Task FindHardBlockingRefusalAsync_ForAScenarioWork_ReportsNothingAndRunsNoCheck_LikeTheWrite()
    {
        _conflictChecker.CheckAsync(Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new PreCommitCheckResult([HardBlocking()]));
        var work = NewWork();
        work.AnalyseToken = Guid.NewGuid();

        var refusal = await _sut.FindHardBlockingRefusalAsync(work, CancellationToken.None);
        var thrown = await CatchAsync(() => _sut.EnsureNoHardBlockingConflictAsync(work, CancellationToken.None));

        refusal.ShouldBeNull();
        thrown.ShouldBeNull();
        await _conflictChecker.DidNotReceiveWithAnyArgs().CheckAsync(default!, default(Guid?), default);
    }

    [Test]
    public async Task FindHardBlockingRefusalAsync_ForAWork_ChecksTheRowPlannedWorkRowFromBuilds()
    {
        var work = NewWork();

        await _sut.FindHardBlockingRefusalAsync(work, CancellationToken.None);

        await _conflictChecker.Received(1).CheckAsync(
            Arg.Is<IReadOnlyList<PlannedWorkRow>>(rows => rows.Count == 1 && rows[0] == PlannedWorkRow.From(work)),
            null,
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task FindHardBlockingRefusalAsync_WithVacatedRows_UsesTheRemovalOverloadAndTheUpdateWording()
    {
        _conflictChecker.CheckAsync(
                Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<IReadOnlyList<PlannedRemovalRow>>(),
                Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new PreCommitCheckResult([HardBlocking()]));
        var vacated = new PlannedRemovalRow(_clientId, _date, new TimeOnly(8, 0), new TimeOnly(16, 0), Guid.NewGuid());

        var refusal = await _sut.FindHardBlockingRefusalAsync(
            [new PlannedWorkRow(_clientId, _date, new TimeOnly(9, 0), new TimeOnly(17, 0), _shiftId)],
            [vacated],
            WorkWriteOperation.Update,
            CancellationToken.None);

        refusal.ShouldNotBeNull();
        refusal.Code.ShouldBe(WorkWriteConflictCodes.BlockedByConflicts);
        refusal.Message.ShouldBe(
            $"Work update blocked: client {_clientId} would introduce 1 non-overridable schedule conflict(s) on 2027-03-10. Not committed.");
        await _conflictChecker.DidNotReceiveWithAnyArgs().CheckAsync(default!, default(Guid?), default);
    }

    [Test]
    public async Task FindHardBlockingRefusalAsync_BulkOverSeveralShifts_KeepsTheBulkWordingAndNamesNoSingleShift()
    {
        _conflictChecker.CheckAsync(Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new PreCommitCheckResult([Collision(), HardBlocking()]));

        var refusal = await _sut.FindHardBlockingRefusalAsync(
            [
                new PlannedWorkRow(_clientId, _date, new TimeOnly(8, 0), new TimeOnly(16, 0), _shiftId),
                new PlannedWorkRow(_clientId, _date.AddDays(1), new TimeOnly(8, 0), new TimeOnly(16, 0), Guid.NewGuid()),
            ],
            null,
            WorkWriteOperation.Bulk,
            CancellationToken.None);

        refusal.ShouldNotBeNull();
        refusal.Message.ShouldBe(
            "Bulk write blocked: at least one row would introduce 2 non-overridable schedule conflict(s). Nothing was committed.");
        refusal.Details[WorkWriteConflictCodes.ShiftIdField].ShouldBeNull();
        refusal.Details[WorkWriteConflictCodes.DateField].ShouldBeNull();
        refusal.Details[WorkWriteConflictCodes.ConflictsField].ShouldBeOfType<List<WorkConflictItem>>().Count.ShouldBe(1);
        await _shiftRepository.DidNotReceiveWithAnyArgs().GetSporadicInfoAsync(default, default);
    }

    private static async Task<WorkWriteConflictException?> CatchAsync(Func<Task> act)
    {
        try
        {
            await act();
            return null;
        }
        catch (WorkWriteConflictException exception)
        {
            return exception;
        }
    }

    private static void AssertCoupled(WorkBookingRefusal? refusal, WorkWriteConflictException? thrown)
    {
        if (refusal == null)
        {
            thrown.ShouldBeNull();
            return;
        }

        thrown.ShouldNotBeNull();
        thrown.ErrorCode.ShouldBe(refusal.Code);
        thrown.Message.ShouldBe(refusal.Message);
        thrown.Details.Keys.ShouldBe(refusal.Details.Keys, ignoreOrder: true);
        foreach (var (field, value) in refusal.Details)
        {
            if (value is List<WorkConflictItem> items)
            {
                thrown.Details[field].ShouldBeOfType<List<WorkConflictItem>>().ShouldBe(items);
            }
            else
            {
                thrown.Details[field].ShouldBe(value);
            }
        }
    }

    private Work NewWork() => new()
    {
        ClientId = _clientId,
        ShiftId = _shiftId,
        CurrentDate = _date,
        StartTime = new TimeOnly(8, 0),
        EndTime = new TimeOnly(16, 0)
    };

    private void GivenSporadicUsage(int engagedAtDay, int distinctBookedDays, int sumEmployees, int quantity)
    {
        _shiftRepository.GetSporadicInfoAsync(_shiftId, Arg.Any<CancellationToken>())
            .Returns(SporadicInfo(isSporadic: true, sumEmployees, quantity));
        _workRepository.GetSporadicCapacityUsageAsync(
                _shiftId, _date, Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new SporadicCapacityUsage(engagedAtDay, distinctBookedDays));
    }

    private SporadicShiftInfo SporadicInfo(bool isSporadic, int sumEmployees, int quantity) =>
        new(ShiftName, isSporadic, ShiftSporadic.Month, _date.AddMonths(-1), null, sumEmployees, quantity);

    private ScheduleValidationNotificationDto HardBlocking() => new()
    {
        Type = ScheduleValidationType.Error,
        ClientId = _clientId,
        Date = _date,
        Comment = "qualification-missing"
    };

    private ScheduleValidationNotificationDto Collision() => new()
    {
        Type = ScheduleValidationType.Error,
        ClientId = _clientId,
        Date = _date,
        Comment = ScheduleValidationKeys.Collision
    };
}
