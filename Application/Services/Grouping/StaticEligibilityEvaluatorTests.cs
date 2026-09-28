// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the static eligibility rules of the grouping analysis: each of the five conditions blocks on its
/// own, the first eligible run day short-circuits, the reason blocking on the most run days wins (a tie
/// goes to the lower enum index), and the weekday set contains only weekdays with an eligible run day.
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
        IReadOnlyList<ClientAvailability>? availability = null)
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
            ExpiredMandatoryBlocks: false);

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
    public void Evaluate_InactiveContract_ReportsNoActiveContract()
    {
        Build(contractFor: _ => Contract(active: false)).Evaluate(ClientId, ShiftId).Reason
            .ShouldBe(GroupingIneligibilityReason.NoActiveContract);
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
    public void EligibleWeekdays_ContainsOnlyWeekdaysWithAnEligibleRunDay()
    {
        var evaluator = Build(runDays: [Monday, Tuesday], contractFor: _ => Contract(tuesday: false));

        evaluator.EligibleWeekdays(ClientId, ShiftId).ShouldBe(new[] { DayOfWeek.Monday }, ignoreOrder: true);
    }
}
