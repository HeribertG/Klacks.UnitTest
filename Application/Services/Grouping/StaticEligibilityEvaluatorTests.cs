// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the static eligibility rules of the grouping analysis: each of the five conditions blocks on its
/// own, the first eligible run day short-circuits, the reason blocking on the most run days wins (a tie
/// goes to the lower enum index), and the weekday set contains only weekdays with an eligible run day.
/// Settings-default data (HasActiveContract false) is evaluated like a contract with its weekday and shift-work
/// flags; only a day without any contract data is NoActiveContract, and HasActiveContractInPeriod is informational.
/// A run day outside the client's membership window (entry and exit inclusive) is OutsideMembership.
/// </summary>

using Klacks.Api.Application.DTOs.Grouping;
using Klacks.Api.Application.Services.Grouping;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Staffs;

namespace Klacks.UnitTest.Application.Services.Grouping;

[TestFixture]
public class StaticEligibilityEvaluatorTests
{
    private static readonly Guid ClientId = Guid.NewGuid();
    private static readonly Guid ShiftId = Guid.NewGuid();
    private static readonly Guid QualificationId = Guid.NewGuid();
    private static readonly DateOnly Monday = new(2026, 6, 15);
    private static readonly DateOnly Tuesday = new(2026, 6, 16);
    private static readonly DateOnly Wednesday = new(2026, 6, 17);
    private static readonly DateOnly Thursday = new(2026, 6, 18);
    private static readonly DateOnly Friday = new(2026, 6, 19);
    private static readonly DateOnly NextTuesday = new(2026, 6, 23);
    private static readonly TimeOnly EarlyStart = new(6, 0);
    private static readonly TimeOnly EarlyEnd = new(14, 0);
    private static readonly TimeOnly LateStart = new(16, 0);
    private static readonly TimeOnly LateEnd = new(23, 0);

    private static EffectiveContractData Contract(bool active = true, bool shiftWork = true, bool monday = true, bool tuesday = true) => new()
    {
        HasActiveContract = active,
        PerformsShiftWork = shiftWork,
        WorkOnMonday = monday,
        WorkOnTuesday = tuesday,
    };

    private static StaticEligibilityEvaluator Build(
        IReadOnlyList<DateOnly>? runDays = null,
        Func<DateOnly, EffectiveContractData?>? contractFor = null,
        TimeOnly? start = null,
        TimeOnly? end = null,
        IReadOnlyList<ShiftRequiredQualification>? requirements = null,
        IReadOnlyList<ClientQualification>? qualifications = null,
        bool blacklisted = false,
        IReadOnlyList<ClientAvailability>? availability = null,
        MembershipWindow? membership = null)
    {
        var days = runDays ?? [Monday];
        contractFor ??= _ => Contract();
        var shift = new GroupingShiftRecord(ShiftId, "Early", start ?? EarlyStart, end ?? EarlyEnd, 1);
        var contracts = days.ToDictionary(
            day => day,
            day =>
            {
                var perClient = new Dictionary<Guid, EffectiveContractData>();
                var contract = contractFor(day);
                if (contract != null)
                {
                    perClient[ClientId] = contract;
                }

                return (IReadOnlyDictionary<Guid, EffectiveContractData>)perClient;
            });
        var availabilityByDay = (availability ?? [])
            .GroupBy(entry => (entry.ClientId, entry.Date))
            .ToDictionary(group => (group.Key.ClientId, group.Key.Date), group => (IReadOnlyList<ClientAvailability>)group.ToList());

        var context = new GroupingEligibilityContext(
            new Dictionary<Guid, GroupingShiftRecord> { [ShiftId] = shift },
            new Dictionary<Guid, IReadOnlyList<DateOnly>> { [ShiftId] = days },
            contracts,
            new Dictionary<Guid, IReadOnlyList<ShiftRequiredQualification>> { [ShiftId] = requirements ?? [] },
            new Dictionary<Guid, IReadOnlyList<ClientQualification>> { [ClientId] = qualifications ?? [] },
            blacklisted ? new HashSet<GroupingEntityPair> { new(ClientId, ShiftId) } : new HashSet<GroupingEntityPair>(),
            availabilityByDay,
            ExpiredMandatoryBlocks: false,
            membership is null
                ? new Dictionary<Guid, MembershipWindow>()
                : new Dictionary<Guid, MembershipWindow> { [ClientId] = membership });

        return new StaticEligibilityEvaluator(context);
    }

    [Test]
    public void Evaluate_ActiveContractAndNoRestriction_IsEligible()
    {
        Build().Evaluate(ClientId, ShiftId).IsEligible.ShouldBeTrue();
    }

    [Test]
    public void Evaluate_NoContractOnRunDay_ReportsNoActiveContract()
    {
        var verdict = Build(contractFor: _ => null).Evaluate(ClientId, ShiftId);

        verdict.IsEligible.ShouldBeFalse();
        verdict.Reason.ShouldBe(GroupingIneligibilityReason.NoActiveContract);
    }

    [Test]
    public void SettingsDefaultsOnATickedWeekday_AreEligible()
    {
        var evaluator = Build(contractFor: _ => Contract(active: false));

        evaluator.Evaluate(ClientId, ShiftId).ShouldBe(EligibilityVerdict.Eligible);
        evaluator.IsEligible(ClientId, ShiftId).ShouldBeTrue();
        evaluator.EligibleWeekdays(ClientId, ShiftId).ShouldBe(new[] { DayOfWeek.Monday }, ignoreOrder: true);
        evaluator.HasActiveContractInPeriod(ClientId).ShouldBeFalse();
    }

    [Test]
    public void SettingsDefaultsOnAnUntickedWeekday_ReportWeekdayNotAllowed()
    {
        var evaluator = Build(contractFor: _ => Contract(active: false, monday: false));

        evaluator.Evaluate(ClientId, ShiftId).Reason.ShouldBe(GroupingIneligibilityReason.WeekdayNotAllowed);
        evaluator.IsEligible(ClientId, ShiftId).ShouldBeFalse();
    }

    [Test]
    public void SettingsDefaultsWithoutShiftWorkOnALateShift_ReportNotShiftWorker()
    {
        var evaluator = Build(contractFor: _ => Contract(active: false, shiftWork: false), start: LateStart, end: LateEnd);

        evaluator.Evaluate(ClientId, ShiftId).Reason.ShouldBe(GroupingIneligibilityReason.NotShiftWorker);
        evaluator.IsEligible(ClientId, ShiftId).ShouldBeFalse();
    }

    [Test]
    public void HasActiveContractInPeriod_IsTrueAsSoonAsOneDayHasAnActiveContract()
    {
        var evaluator = Build(runDays: [Monday, Tuesday], contractFor: day => Contract(active: day == Tuesday));

        evaluator.HasActiveContractInPeriod(ClientId).ShouldBeTrue();
    }

    [Test]
    public void HasActiveContractInPeriod_IsFalseWithoutAnyContractData()
    {
        Build(contractFor: _ => null).HasActiveContractInPeriod(ClientId).ShouldBeFalse();
    }

    [Test]
    public void Evaluate_WeekdayNotAllowed_ReportsWeekdayNotAllowed()
    {
        Build(contractFor: _ => Contract(monday: false)).Evaluate(ClientId, ShiftId).Reason
            .ShouldBe(GroupingIneligibilityReason.WeekdayNotAllowed);
    }

    [Test]
    public void Evaluate_NonShiftWorkerOnLateShift_ReportsNotShiftWorker()
    {
        Build(contractFor: _ => Contract(shiftWork: false), start: LateStart, end: LateEnd)
            .Evaluate(ClientId, ShiftId).Reason.ShouldBe(GroupingIneligibilityReason.NotShiftWorker);
    }

    [Test]
    public void Evaluate_NonShiftWorkerOnEarlyShift_IsEligible()
    {
        Build(contractFor: _ => Contract(shiftWork: false)).Evaluate(ClientId, ShiftId).IsEligible.ShouldBeTrue();
    }

    [Test]
    public void Evaluate_MissingMandatoryQualification_ReportsMandatoryQualificationMissing()
    {
        var requirement = new ShiftRequiredQualification
        {
            ShiftId = ShiftId, QualificationId = QualificationId, IsMandatory = true, MinLevel = QualificationLevel.Proficient
        };

        Build(requirements: [requirement]).Evaluate(ClientId, ShiftId).Reason
            .ShouldBe(GroupingIneligibilityReason.MandatoryQualificationMissing);
    }

    [Test]
    public void Evaluate_HeldMandatoryQualification_IsEligible()
    {
        var requirement = new ShiftRequiredQualification
        {
            ShiftId = ShiftId, QualificationId = QualificationId, IsMandatory = true, MinLevel = QualificationLevel.Proficient
        };
        var held = new ClientQualification { ClientId = ClientId, QualificationId = QualificationId, Level = QualificationLevel.Expert };

        Build(requirements: [requirement], qualifications: [held]).Evaluate(ClientId, ShiftId).IsEligible.ShouldBeTrue();
    }

    [Test]
    public void Evaluate_MandatoryQualificationBelowABindingLevel_ReportsMandatoryQualificationLevelTooLow()
    {
        var requirement = new ShiftRequiredQualification
        {
            ShiftId = ShiftId, QualificationId = QualificationId, IsMandatory = true, MinLevel = QualificationLevel.Proficient,
            LevelBindingOverride = true
        };
        var held = new ClientQualification { ClientId = ClientId, QualificationId = QualificationId, Level = QualificationLevel.Basic };

        var verdict = Build(requirements: [requirement], qualifications: [held]).Evaluate(ClientId, ShiftId);

        verdict.IsEligible.ShouldBeFalse();
        verdict.Reason.ShouldBe(GroupingIneligibilityReason.MandatoryQualificationLevelTooLow);
    }

    [Test]
    public void Evaluate_MandatoryQualificationBelowANonBindingLevel_IsEligible()
    {
        var requirement = new ShiftRequiredQualification
        {
            ShiftId = ShiftId, QualificationId = QualificationId, IsMandatory = true, MinLevel = QualificationLevel.Proficient,
            LevelBindingOverride = false,
            Qualification = new Qualification { Id = QualificationId, IsLevelBinding = true }
        };
        var held = new ClientQualification { ClientId = ClientId, QualificationId = QualificationId, Level = QualificationLevel.Basic };

        Build(requirements: [requirement], qualifications: [held]).Evaluate(ClientId, ShiftId).IsEligible.ShouldBeTrue();
    }

    [Test]
    public void Evaluate_Blacklisted_ReportsBlacklisted()
    {
        Build(blacklisted: true).Evaluate(ClientId, ShiftId).Reason.ShouldBe(GroupingIneligibilityReason.Blacklisted);
    }

    [Test]
    public void Evaluate_UnavailableDuringShift_ReportsUnavailable()
    {
        var blocked = Enumerable.Range(EarlyStart.Hour, EarlyEnd.Hour - EarlyStart.Hour)
            .Select(hour => new ClientAvailability { ClientId = ClientId, Date = Monday, Hour = hour, IsAvailable = false })
            .ToList();

        Build(availability: blocked).Evaluate(ClientId, ShiftId).Reason.ShouldBe(GroupingIneligibilityReason.Unavailable);
    }

    [Test]
    public void Evaluate_FirstDayBlockedSecondDayFine_IsEligible()
    {
        var evaluator = Build(runDays: [Monday, Tuesday], contractFor: _ => Contract(monday: false));

        evaluator.Evaluate(ClientId, ShiftId).IsEligible.ShouldBeTrue();
    }

    [Test]
    public void Evaluate_DifferentReasonsOnEquallyManyDays_ReportsLowerEnumIndex()
    {
        var evaluator = Build(
            runDays: [Monday, Tuesday],
            contractFor: day => day == Monday ? null : Contract(tuesday: false));

        evaluator.Evaluate(ClientId, ShiftId).Reason.ShouldBe(GroupingIneligibilityReason.NoActiveContract);
    }

    [Test]
    public void Evaluate_ReasonBlockingMostRunDays_WinsOverHigherPriorityReason()
    {
        var evaluator = Build(
            runDays: [Monday, Tuesday, Wednesday, Thursday, Friday, NextTuesday],
            contractFor: day => day == Monday ? null : Contract(),
            blacklisted: true);

        evaluator.Evaluate(ClientId, ShiftId).Reason.ShouldBe(GroupingIneligibilityReason.Blacklisted);
    }

    [Test]
    public void Evaluate_HigherPriorityReasonOnMostRunDays_Wins()
    {
        var evaluator = Build(
            runDays: [Monday, Tuesday, Wednesday, Thursday, Friday, NextTuesday],
            contractFor: day => day == Monday ? Contract() : null,
            blacklisted: true);

        evaluator.Evaluate(ClientId, ShiftId).Reason.ShouldBe(GroupingIneligibilityReason.NoActiveContract);
    }

    [Test]
    public void ExitedEmployeeWithSettingsDefaults_IsNotEligibleAfterTheExit()
    {
        var evaluator = Build(
            runDays: [Tuesday, NextTuesday],
            contractFor: _ => Contract(active: false),
            membership: new MembershipWindow(Monday.AddDays(-30), Monday));

        evaluator.Evaluate(ClientId, ShiftId).Reason.ShouldBe(GroupingIneligibilityReason.OutsideMembership);
        evaluator.IsEligible(ClientId, ShiftId).ShouldBeFalse();
        evaluator.EligibleWeekdays(ClientId, ShiftId).ShouldBeEmpty();
    }

    [Test]
    public void ExitDay_IsStillAMemberDay()
    {
        var evaluator = Build(
            runDays: [Monday, Tuesday],
            contractFor: _ => Contract(active: false),
            membership: new MembershipWindow(Monday.AddDays(-30), Monday));

        evaluator.EligibleWeekdays(ClientId, ShiftId).ShouldBe(new[] { DayOfWeek.Monday }, ignoreOrder: true);
    }

    [Test]
    public void EntryInsideThePeriod_MakesOnlyTheDaysFromTheEntryEligible()
    {
        var evaluator = Build(
            runDays: [Monday, NextTuesday],
            contractFor: _ => Contract(active: false, monday: true, tuesday: true),
            membership: new MembershipWindow(Tuesday, null));

        evaluator.Evaluate(ClientId, ShiftId).IsEligible.ShouldBeTrue();
        evaluator.IsEligible(ClientId, ShiftId).ShouldBeTrue();
        evaluator.EligibleWeekdays(ClientId, ShiftId).ShouldBe(new[] { DayOfWeek.Tuesday }, ignoreOrder: true);
    }

    [Test]
    public void ActiveContractOnlyOutsideTheMembership_DoesNotCountAsActiveContractInPeriod()
    {
        var evaluator = Build(
            runDays: [Monday, Tuesday],
            contractFor: day => Contract(active: day == Monday),
            membership: new MembershipWindow(Tuesday, null));

        evaluator.HasActiveContractInPeriod(ClientId).ShouldBeFalse();
    }

    [Test]
    public void EligibleWeekdays_ContainsOnlyWeekdaysWithAnEligibleRunDay()
    {
        var evaluator = Build(runDays: [Monday, Tuesday], contractFor: _ => Contract(tuesday: false));

        evaluator.EligibleWeekdays(ClientId, ShiftId).ShouldBe(new[] { DayOfWeek.Monday }, ignoreOrder: true);
    }
}
