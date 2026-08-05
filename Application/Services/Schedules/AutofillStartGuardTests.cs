// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Application.Constants;
using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.Enums;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Services.Schedules;

/// <summary>
/// The guard is the only place that decides whether an autofill run may start. Two properties matter:
/// each family keeps its own size limits (they used to be copied per controller), and the identical
/// selection cannot be started twice - without the lock two planners burn the time budget on the same
/// period and then race for the apply.
/// </summary>
[TestFixture]
public sealed class AutofillStartGuardTests
{
    private static readonly DateOnly From = new(2026, 5, 1);
    private static readonly DateOnly Until = new(2026, 5, 31);
    private static readonly Guid AgentA = Guid.NewGuid();
    private static readonly Guid AgentB = Guid.NewGuid();

    private AutofillStartGuard _sut = null!;

    [SetUp]
    public void SetUp() => _sut = new AutofillStartGuard();

    [Test]
    public void EnsureWithinLimits_Wizard1_TooManyAgents_IsRefused()
    {
        var act = () => _sut.EnsureWithinLimits(
            AutofillFamily.Wizard1, WizardLimits.MaxAgents + 1, 10, From, Until);

        act.ShouldThrow<AutofillLimitExceededException>().Code.ShouldBe(WizardLimits.TooLargeErrorCode);
    }

    [Test]
    public void EnsureWithinLimits_Wizard1_AtTheLimit_IsAllowed()
    {
        Should.NotThrow(() => _sut.EnsureWithinLimits(
            AutofillFamily.Wizard1, WizardLimits.MaxAgents, WizardLimits.MaxShifts, From, Until));
    }

    [Test]
    public void EnsureWithinLimits_Harmonizer_PeriodTooLong_IsRefusedWithThePeriodCode()
    {
        var act = () => _sut.EnsureWithinLimits(
            AutofillFamily.Harmonizer, 10, 0, From, From.AddDays(AutofillLimits.MaxPeriodDays));

        act.ShouldThrow<AutofillLimitExceededException>().Code.ShouldBe(AutofillLimits.PeriodTooLongErrorCode);
    }

    [Test]
    public void EnsureWithinLimits_Harmonizer_ManyShifts_AreIrrelevant()
    {
        // The bitmap engines scale with agents x days, not with the number of distinct shifts.
        Should.NotThrow(() => _sut.EnsureWithinLimits(
            AutofillFamily.Harmonizer, 10, WizardLimits.MaxShifts * 10, From, Until));
    }

    [Test]
    public void EnsureWithinLimits_AutoWizard_DecisionSpaceTooLarge_CarriesTheMeasuredProduct()
    {
        const int agents = 200;
        const int shifts = 50;

        var act = () => _sut.EnsureWithinLimits(AutofillFamily.AutoWizard, agents, shifts, From, Until);

        var exception = act.ShouldThrow<AutofillLimitExceededException>();
        exception.Code.ShouldBe(AutoWizardLimits.TooLargeErrorCode);
        exception.SlotProduct.ShouldBe((long)agents * shifts * 31);
        exception.MaxSlotProduct.ShouldBe(AutoWizardLimits.MaxSlotProduct);
    }

    [Test]
    public void EnsureWithinLimits_SingleDayPeriod_CountsAsOneDay()
    {
        // from == until is one day, not zero: 250 x 80 x 1 = 20'000 still fits, one agent more does not.
        Should.NotThrow(() => _sut.EnsureWithinLimits(
            AutofillFamily.AutoWizard, AutoWizardLimits.MaxAgents, AutoWizardLimits.MaxShifts, From, From));

        var act = () => _sut.EnsureWithinLimits(
            AutofillFamily.AutoWizard, AutoWizardLimits.MaxAgents + 1, AutoWizardLimits.MaxShifts, From, From);

        act.ShouldThrow<AutofillLimitExceededException>().PeriodDays.ShouldBe(1);
    }

    [Test]
    public void AcquireRunLock_SameSelectionTwice_IsRefusedWithTheRunningJob()
    {
        var firstJob = Guid.NewGuid();
        _sut.AcquireRunLock(AutofillFamily.Wizard1, From, Until, null, [AgentA, AgentB], firstJob);

        var act = () => _sut.AcquireRunLock(
            AutofillFamily.Wizard1, From, Until, null, [AgentA, AgentB], Guid.NewGuid());

        act.ShouldThrow<AutofillRunConflictException>().RunningJobId.ShouldBe(firstJob);
    }

    [Test]
    public void AcquireRunLock_SameAgentsInDifferentOrder_IsTheSameSelection()
    {
        _sut.AcquireRunLock(AutofillFamily.Wizard1, From, Until, null, [AgentA, AgentB], Guid.NewGuid());

        var act = () => _sut.AcquireRunLock(
            AutofillFamily.Wizard1, From, Until, null, [AgentB, AgentA], Guid.NewGuid());

        act.ShouldThrow<AutofillRunConflictException>();
    }

    [Test]
    public void AcquireRunLock_DifferentFamily_DoesNotCollide()
    {
        _sut.AcquireRunLock(AutofillFamily.Wizard1, From, Until, null, [AgentA], Guid.NewGuid());

        Should.NotThrow(() => _sut.AcquireRunLock(
            AutofillFamily.Harmonizer, From, Until, null, [AgentA], Guid.NewGuid()));
    }

    [Test]
    public void AcquireRunLock_DifferentScenario_DoesNotCollide()
    {
        _sut.AcquireRunLock(AutofillFamily.Wizard1, From, Until, null, [AgentA], Guid.NewGuid());

        Should.NotThrow(() => _sut.AcquireRunLock(
            AutofillFamily.Wizard1, From, Until, Guid.NewGuid(), [AgentA], Guid.NewGuid()));
    }

    [Test]
    public void AcquireRunLock_DifferentPeriod_DoesNotCollide()
    {
        _sut.AcquireRunLock(AutofillFamily.Wizard1, From, Until, null, [AgentA], Guid.NewGuid());

        Should.NotThrow(() => _sut.AcquireRunLock(
            AutofillFamily.Wizard1, From.AddDays(1), Until, null, [AgentA], Guid.NewGuid()));
    }

    [Test]
    public void ReleaseRunLock_FreesTheSelectionForTheNextRun()
    {
        var firstJob = Guid.NewGuid();
        _sut.AcquireRunLock(AutofillFamily.Wizard1, From, Until, null, [AgentA], firstJob);

        _sut.ReleaseRunLock(firstJob);

        Should.NotThrow(() => _sut.AcquireRunLock(
            AutofillFamily.Wizard1, From, Until, null, [AgentA], Guid.NewGuid()));
    }

    [Test]
    public void ReleaseRunLock_UnknownJob_IsANoOp()
    {
        Should.NotThrow(() => _sut.ReleaseRunLock(Guid.NewGuid()));
    }

    [Test]
    public void AcquireRunLock_ConcurrentAttempts_LetExactlyOneThrough()
    {
        const int attempts = 32;
        var conflicts = 0;

        Parallel.For(0, attempts, _ =>
        {
            try
            {
                _sut.AcquireRunLock(AutofillFamily.Wizard1, From, Until, null, [AgentA], Guid.NewGuid());
            }
            catch (AutofillRunConflictException)
            {
                Interlocked.Increment(ref conflicts);
            }
        });

        conflicts.ShouldBe(attempts - 1);
    }
}
