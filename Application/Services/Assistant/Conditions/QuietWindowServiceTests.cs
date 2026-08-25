// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Covers the three independent quiet reasons (running optimizer job, active ERP import, sealed/locked
/// target entity) individually, their OR-combination, and that the cheap in-memory checks (a)/(b)
/// short-circuit before the DB-backed checks in (c) ever run.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Conditions;
using Klacks.Api.Application.Services.Imports;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Schedules;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Services.Assistant.Conditions;

[TestFixture]
public sealed class QuietWindowServiceTests
{
    private static readonly DateOnly ShiftDate = new(2026, 8, 25);
    private static readonly Guid ShiftId = Guid.NewGuid();
    private static readonly Guid ConditionGroupId = Guid.NewGuid();
    private static readonly Guid OtherGroupId = Guid.NewGuid();

    private AutofillStartGuard _autofillStartGuard = null!;
    private ErpImportRunState _erpImportRunState = null!;
    private IShiftRepository _shiftRepository = null!;
    private IWorkRepository _workRepository = null!;
    private ISealedDayRepository _sealedDayRepository = null!;
    private QuietWindowService _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _autofillStartGuard = new AutofillStartGuard();
        _erpImportRunState = new ErpImportRunState();
        _shiftRepository = Substitute.For<IShiftRepository>();
        _workRepository = Substitute.For<IWorkRepository>();
        _sealedDayRepository = Substitute.For<ISealedDayRepository>();

        _shiftRepository.GetNoTracking(ShiftId).Returns(new Shift { Id = ShiftId, FromDate = ShiftDate });
        _workRepository.HasLockedWorkForShiftAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(false);
        _sealedDayRepository.GetRangeAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new List<SealedDay>());

        _sut = new QuietWindowService(_autofillStartGuard, _erpImportRunState, _shiftRepository, _workRepository, _sealedDayRepository);
    }

    private static AgentCondition Condition(Guid? entityId, Guid? groupId = null) => new()
    {
        Id = Guid.NewGuid(),
        TriggerKind = AgentTriggerKinds.OpenOrder,
        Fingerprint = "open_order:" + entityId,
        EntityId = entityId,
        GroupId = groupId,
        Severity = AgentTriggerSeverity.High,
        PayloadJson = "{}"
    };

    [Test]
    public async Task IsQuietForAsync_NothingActive_IsFalse()
    {
        var quiet = await _sut.IsQuietForAsync(Condition(ShiftId));

        quiet.ShouldBeFalse();
    }

    [Test]
    public async Task IsQuietForAsync_OptimizerJobRunning_IsTrueWithoutTouchingTheRepositories()
    {
        _autofillStartGuard.AcquireRunLock(AutofillFamily.Wizard1, ShiftDate, ShiftDate, null, [Guid.NewGuid()], Guid.NewGuid());

        var quiet = await _sut.IsQuietForAsync(Condition(ShiftId));

        quiet.ShouldBeTrue();
        await _shiftRepository.DidNotReceive().GetNoTracking(Arg.Any<Guid>());
        await _workRepository.DidNotReceive().HasLockedWorkForShiftAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _sealedDayRepository.DidNotReceive().GetRangeAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task IsQuietForAsync_ErpImportRunning_IsTrueWithoutTouchingTheRepositories()
    {
        _erpImportRunState.MarkStarted();

        var quiet = await _sut.IsQuietForAsync(Condition(ShiftId));

        quiet.ShouldBeTrue();
        await _shiftRepository.DidNotReceive().GetNoTracking(Arg.Any<Guid>());
        await _workRepository.DidNotReceive().HasLockedWorkForShiftAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _sealedDayRepository.DidNotReceive().GetRangeAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task IsQuietForAsync_TargetWorkIsLocked_IsTrue()
    {
        _workRepository.HasLockedWorkForShiftAsync(ShiftId, Arg.Any<CancellationToken>()).Returns(true);

        var quiet = await _sut.IsQuietForAsync(Condition(ShiftId));

        quiet.ShouldBeTrue();
    }

    [Test]
    public async Task IsQuietForAsync_TargetWorkLocked_DoesNotAlsoQueryTheSealedDayRepository()
    {
        _workRepository.HasLockedWorkForShiftAsync(ShiftId, Arg.Any<CancellationToken>()).Returns(true);

        await _sut.IsQuietForAsync(Condition(ShiftId));

        await _sealedDayRepository.DidNotReceive().GetRangeAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task IsQuietForAsync_TargetDaySealedForItsGroup_IsTrue()
    {
        _sealedDayRepository.GetRangeAsync(ShiftDate, ShiftDate, ConditionGroupId, Arg.Any<CancellationToken>())
            .Returns(new List<SealedDay> { new() { Date = ShiftDate, GroupId = ConditionGroupId } });

        var quiet = await _sut.IsQuietForAsync(Condition(ShiftId, ConditionGroupId));

        quiet.ShouldBeTrue();
    }

    [Test]
    public async Task IsQuietForAsync_UngroupedConditionUnderAGlobalSeal_IsTrue()
    {
        _sealedDayRepository.GetRangeAsync(ShiftDate, ShiftDate, null, Arg.Any<CancellationToken>())
            .Returns(new List<SealedDay> { new() { Date = ShiftDate, GroupId = null } });

        var quiet = await _sut.IsQuietForAsync(Condition(ShiftId, groupId: null));

        quiet.ShouldBeTrue();
    }

    [Test]
    public async Task IsQuietForAsync_UngroupedConditionWithOnlyAnUnrelatedGroupSeal_IsFalse()
    {
        // GetRangeAsync applies no group filter at all when groupId is null, so it can hand back rows
        // for OTHER groups; an ungrouped condition must not be treated as sealed by a group it has
        // nothing to do with - only a GroupId == null (global) row may quiet it.
        _sealedDayRepository.GetRangeAsync(ShiftDate, ShiftDate, null, Arg.Any<CancellationToken>())
            .Returns(new List<SealedDay> { new() { Date = ShiftDate, GroupId = OtherGroupId } });

        var quiet = await _sut.IsQuietForAsync(Condition(ShiftId, groupId: null));

        quiet.ShouldBeFalse();
    }

    [Test]
    public async Task IsQuietForAsync_EntityIdNull_IsFalseWithoutTouchingTheShiftRepository()
    {
        var quiet = await _sut.IsQuietForAsync(Condition(entityId: null));

        quiet.ShouldBeFalse();
        await _shiftRepository.DidNotReceive().GetNoTracking(Arg.Any<Guid>());
    }

    [Test]
    public async Task IsQuietForAsync_TargetShiftNoLongerExists_IsFalse()
    {
        var missingShiftId = Guid.NewGuid();
        _shiftRepository.GetNoTracking(missingShiftId).Returns((Shift?)null);

        var quiet = await _sut.IsQuietForAsync(Condition(missingShiftId));

        quiet.ShouldBeFalse();
        await _workRepository.DidNotReceive().HasLockedWorkForShiftAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task IsQuietForAsync_OptimizerJobAndErpImportBothActive_IsTrueOnce()
    {
        _autofillStartGuard.AcquireRunLock(AutofillFamily.Wizard1, ShiftDate, ShiftDate, null, [Guid.NewGuid()], Guid.NewGuid());
        _erpImportRunState.MarkStarted();

        var quiet = await _sut.IsQuietForAsync(Condition(ShiftId));

        quiet.ShouldBeTrue();
    }
}
