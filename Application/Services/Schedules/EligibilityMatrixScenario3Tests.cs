// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Api-level half of the autofill scenario 3 suite (tests/autofill/SPEC-SZENARIO3.md, owner
/// decisions S3-1/S3-2): EligibilityMatrixBuilder.BuildAsync against real ClientQualification /
/// ShiftRequiredQualification rows for the March 2026 fixture (5 employees, early/late/night,
/// NACHT-BEF / OBJEKT-A / ERSTE-HILFE). Documents production findings K1 (expired mandatory
/// qualification is only a Warning by default), K2 (no requirement inheritance from the order
/// root shift to its cut shifts) and K7 (expired AND below MinLevel falls back to Missing and
/// becomes a hard veto even at default settings).
/// </summary>

using System.Globalization;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.Common;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.ScheduleOptimizer.Models;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Services.Schedules;

[TestFixture]
public sealed class EligibilityMatrixScenario3Tests
{
    private const string NachtBefName = "NACHT-BEF";
    private const string ObjektAName = "OBJEKT-A";
    private const string ErsteHilfeName = "ERSTE-HILFE";
    private const string SettingEnabledValue = "true";
    private const string IsoDateFormat = "yyyy-MM-dd";

    private const string EarlyShiftName = "Early";
    private const string LateShiftName = "Late";
    private const string NightShiftName = "Night";
    private const string EarlyStartTime = "07:00";
    private const string EarlyEndTime = "15:00";
    private const string LateStartTime = "15:00";
    private const string LateEndTime = "23:00";
    private const string NightStartTime = "23:00";
    private const string NightEndTime = "07:00";
    private const double ShiftDurationHours = 8;
    private const int SingleCoverage = 1;
    private const int DefaultPriority = 0;

    // Level pinning per owner decision S3-1: every gap is guaranteed to fall into the
    // Missing/Expired branch and never accidentally into InsufficientLevel.
    private const QualificationLevel PinnedHeldLevel = QualificationLevel.Expert;
    private const QualificationLevel PinnedMinLevel = QualificationLevel.Low;

    private const int ExpectedDefaultVetoCount = 62;
    private const int ExpectedSettingVetoCount = 73;
    private const int ExpectedAllMissingVetoCount = 155;
    private const int ExpectedUnfillableNightSlots = 31;

    private static readonly Guid Ma1 = Guid.Parse("00000000-0000-0000-00a0-000000000001");
    private static readonly Guid Ma2 = Guid.Parse("00000000-0000-0000-00a0-000000000002");
    private static readonly Guid Ma3 = Guid.Parse("00000000-0000-0000-00a0-000000000003");
    private static readonly Guid Ma4 = Guid.Parse("00000000-0000-0000-00a0-000000000004");
    private static readonly Guid Ma5 = Guid.Parse("00000000-0000-0000-00a0-000000000005");
    private static readonly Guid[] AllAgents = { Ma1, Ma2, Ma3, Ma4, Ma5 };

    private static readonly Guid EarlyShiftId = Guid.Parse("00000000-0000-0000-00b0-000000000001");
    private static readonly Guid LateShiftId = Guid.Parse("00000000-0000-0000-00b0-000000000002");
    private static readonly Guid NightShiftId = Guid.Parse("00000000-0000-0000-00b0-000000000003");
    private static readonly Guid OrderRootShiftId = Guid.Parse("00000000-0000-0000-00b0-000000000004");

    private static readonly Guid NachtBefQualId = Guid.Parse("00000000-0000-0000-00c0-000000000001");
    private static readonly Guid ObjektAQualId = Guid.Parse("00000000-0000-0000-00c0-000000000002");
    private static readonly Guid ErsteHilfeQualId = Guid.Parse("00000000-0000-0000-00c0-000000000003");

    private static readonly DateOnly PeriodStart = new(2026, 3, 1);
    private static readonly DateOnly PeriodEnd = new(2026, 3, 31);
    private static readonly DateOnly Ma2NachtBefValidUntil = new(2026, 3, 20);
    private static readonly DateOnly BoundaryNightDate = new(2026, 3, 20);
    private static readonly DateOnly FirstExpiredNightDate = new(2026, 3, 21);

    private IClientQualificationRepository _clientRepo = null!;
    private IShiftRequiredQualificationRepository _shiftRepo = null!;
    private ISettingsReader _settingsReader = null!;
    private EligibilityMatrixBuilder _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _clientRepo = Substitute.For<IClientQualificationRepository>();
        _shiftRepo = Substitute.For<IShiftRequiredQualificationRepository>();
        _settingsReader = Substitute.For<ISettingsReader>();
        _sut = new EligibilityMatrixBuilder(_clientRepo, _shiftRepo, _settingsReader);
    }

    [Test]
    public async Task K1_DefaultSetting_ExpiredNachtBef_DoesNotVetoMa2ForNight()
    {
        GivenRequirements(NightRequiresNachtBef());
        GivenHeld(BaseHeldRows());

        var matrix = await BuildAllSlotsAsync();

        for (var date = FirstExpiredNightDate; date <= PeriodEnd; date = date.AddDays(1))
        {
            matrix.Ineligible.ShouldNotContain(
                (Ma2.ToString(), NightShiftId, date),
                $"Finding K1: with QUALIFICATION_EXPIRED_MANDATORY_BLOCKS unset (default false) an expired mandatory NACHT-BEF is only a Warning, so MA-2 must stay assignable for the night shift on {Iso(date)} although the qualification expired on {Iso(Ma2NachtBefValidUntil)}");
            matrix.Gaps[(Ma2.ToString(), NightShiftId, date)].ShouldContain(
                g => g.Reason == QualificationGapReason.Expired && g.Severity == QualificationGapSeverity.Warning,
                $"Finding K1: the expiry on {Iso(date)} must surface as an Expired gap with Warning severity (report only), never as a blocking Error");
        }

        ShouldBeExactlyTriples(
            matrix.Ineligible,
            DefaultRunExpectedTriples(),
            "Finding K1 (default run): only MA-3 and MA-4 are vetoed (Missing is an Error regardless of the setting, 31 night days each = 62 triples); MA-2's expiry adds no veto triple at default settings");
    }

    [Test]
    public async Task S31_SettingEnabled_VetoSetIsExactlyThe73TriplesOfTheEngineHandover()
    {
        // Non-default configuration per owner decision S3-1: QUALIFICATION_EXPIRED_MANDATORY_BLOCKS = true.
        GivenExpiredMandatoryBlocksEnabled();
        GivenRequirements(NightRequiresNachtBef());
        GivenHeld(BaseHeldRows());

        var matrix = await BuildAllSlotsAsync();

        for (var date = PeriodStart; date <= Ma2NachtBefValidUntil; date = date.AddDays(1))
        {
            matrix.Ineligible.ShouldNotContain(
                (Ma2.ToString(), NightShiftId, date),
                $"MA-2 holds NACHT-BEF until {Iso(Ma2NachtBefValidUntil)} and must stay eligible for the night shift on {Iso(date)} even with the escalation setting enabled");
        }

        matrix.Ineligible.ShouldAllBe(
            t => t.AgentId != Ma1.ToString() && t.AgentId != Ma5.ToString(),
            "MA-1 and MA-5 hold NACHT-BEF without expiry and must never be vetoed on any day or shift");

        matrix.Ineligible.Count.ShouldBe(
            ExpectedSettingVetoCount,
            "With the setting enabled the veto set must be exactly the 73-triple engine handover from DISCOVERY-KEYWORDS.md: MA-3 x 31 + MA-4 x 31 (Missing) + MA-2 21.-31.03. x 11 (Expired escalated to Error)");
        ShouldBeExactlyTriples(
            matrix.Ineligible,
            SettingRunExpectedTriples(),
            "Setting run: the veto set must match the 73-triple engine handover exactly (NightShiftId x MA-3 full period, MA-4 full period, MA-2 from 21.03.)");
    }

    [Test]
    public async Task A20_SettingEnabled_NightSlotOnExpiryDate_EligibleBecauseWindowIsInclusiveAndEvaluatedAgainstStart()
    {
        GivenExpiredMandatoryBlocksEnabled();
        GivenRequirements(NightRequiresNachtBef());
        GivenHeld(BaseHeldRows());

        var matrix = await BuildAllSlotsAsync();

        var boundaryKey = (Ma2.ToString(), NightShiftId, BoundaryNightDate);
        matrix.Ineligible.ShouldNotContain(
            boundaryKey,
            "A20: the night shift starting 2026-03-20 23:00 and ending 07:00 on the 21st is one slot whose date is the start date; the validity check runs solely against that start date (evaluatedAgainst = \"start\") and the window is inclusive on both sides (ValidUntil >= date), so MA-2 is still eligible on the expiry date itself");
        matrix.Gaps.ContainsKey(boundaryKey).ShouldBeFalse(
            "A20: on the expiry date the requirement is fully satisfied - no gap at all is recorded, not even a Warning");

        matrix.Ineligible.ShouldContain(
            (Ma2.ToString(), NightShiftId, FirstExpiredNightDate),
            "A20: one day after ValidUntil the same rule vetoes MA-2 (setting enabled); assignment and validity check both use the slot start date, so the boundary behavior is consistent on both sides of the check");
    }

    [Test]
    public async Task A21_IrrelevantErsteHilfeRows_DoNotChangeTheMatrix()
    {
        GivenExpiredMandatoryBlocksEnabled();
        GivenRequirements(NightRequiresNachtBef());

        GivenHeld(BaseHeldRows());
        var without = await BuildAllSlotsAsync();

        var withErsteHilfe = BaseHeldRows();
        withErsteHilfe.Add(Holds(Ma3, ErsteHilfeQualId));
        withErsteHilfe.Add(Holds(Ma4, ErsteHilfeQualId));
        GivenHeld(withErsteHilfe);
        var with = await BuildAllSlotsAsync();

        ShouldBeExactlyTriples(
            with.Ineligible,
            without.Ineligible.ToList(),
            $"A21: {ErsteHilfeName} is required by no shift; adding it to MA-3/MA-4 must not change the veto set in any way");
        with.Gaps.Keys.ToHashSet().SetEquals(without.Gaps.Keys).ShouldBeTrue(
            $"A21: the gap report keys must be identical with and without the irrelevant {ErsteHilfeName} rows");
        foreach (var (key, gaps) in without.Gaps)
        {
            with.Gaps[key].ShouldBe(
                gaps,
                $"A21: the gap payload for {key} must be identical with and without the irrelevant {ErsteHilfeName} rows");
        }
    }

    [Test]
    public async Task K2_A23a_RequirementOnOrderRootShift_GatesNoShiftEvenForAnAgentWithoutTheQualification()
    {
        // MA-4 deliberately does NOT hold OBJEKT-A here: if the order-level requirement had any
        // effect on the three shifts, MA-4 would have to become ineligible somewhere.
        var heldWithoutMa4ObjektA = BaseHeldRows()
            .Where(row => !(row.ClientId == Ma4 && row.QualificationId == ObjektAQualId))
            .ToList();
        GivenRequirements(Requires(OrderRootShiftId, ObjektAQualId, ObjektAName));
        GivenHeld(heldWithoutMa4ObjektA);

        var matrix = await BuildAllSlotsAsync();

        matrix.Ineligible.ShouldBeEmpty(
            $"Production finding K2: a qualification requirement attached to the order root shift is never inherited by its cut/child shifts - the lookup is exact by shift id (ShiftRequiredQualificationRepository.GetByShiftIdsAsync) and the cut path copies nothing, so {ObjektAName} on the order vetoes nobody on any of the three shifts, not even MA-4 who does not hold {ObjektAName} at all");
        matrix.Gaps.ShouldBeEmpty(
            "Production finding K2: the order-level requirement does not even surface as a Warning gap on the child shifts - it is completely invisible to the eligibility matrix");
    }

    [Test]
    public async Task A23b_ObjektARequiredOnEachShift_AllAgentsHoldIt_AddsNoTriples()
    {
        GivenExpiredMandatoryBlocksEnabled();
        GivenHeld(BaseHeldRows());

        GivenRequirements(NightRequiresNachtBef());
        var reference = await BuildAllSlotsAsync();

        GivenRequirements(
            NightRequiresNachtBef(),
            Requires(EarlyShiftId, ObjektAQualId, ObjektAName),
            Requires(LateShiftId, ObjektAQualId, ObjektAName),
            Requires(NightShiftId, ObjektAQualId, ObjektAName));
        var withObjektA = await BuildAllSlotsAsync();

        ShouldBeExactlyTriples(
            withObjektA.Ineligible,
            reference.Ineligible.ToList(),
            $"A23b control: {ObjektAName} required on each of the three shifts individually while all five employees hold it must add zero veto triples compared to the run without {ObjektAName}");
    }

    [Test]
    public async Task A22_L4_NachtBefHeldByNobody_All155NightTriplesVetoed_AndUnfillableReportNamesNachtBef()
    {
        GivenRequirements(NightRequiresNachtBef());
        GivenHeld(BaseHeldRows().Where(row => row.QualificationId != NachtBefQualId).ToList());

        var matrix = await BuildAllSlotsAsync();

        ShouldBeExactlyTriples(
            matrix.Ineligible,
            AllMissingExpectedTriples(),
            $"A22 (L4): with {NachtBefName} removed from every employee the gap reason is Missing, which is a hard veto already at default settings - all 5 employees are ineligible for all 31 night shifts (155 triples), early and late stay unrestricted");

        var report = QualificationGapReportBuilder.BuildUnfillableSlots(matrix, ScenarioCoreShifts(), AllCoreAgents(), []);

        report.Count.ShouldBe(
            ExpectedUnfillableNightSlots,
            "A22: exactly the 31 night slots are unfillable (no eligible employee exists) - one Error detail per slot; the restriction is reported, not silently ignored");
        report.ShouldAllBe(
            d => d.Kind == QualificationGapKind.UnfillableSlot && d.ShiftId == NightShiftId,
            "A22: only night slots may appear in the unfillable report; early and late shifts have eligible employees and ordinary under-supply is not reported here");
        report.ShouldAllBe(
            d => d.QualificationId == NachtBefQualId
                && d.Reason == QualificationGapReason.Missing
                && d.Severity == QualificationGapSeverity.Error,
            $"A22: each unfillable night slot names {NachtBefName} as the missing mandatory qualification with Error severity");
        report.Select(d => d.Date).ToHashSet().SetEquals(AllPeriodDates()).ShouldBeTrue(
            "A22: the unfillable report covers every day of the period 2026-03-01 .. 2026-03-31 exactly once");
    }

    [Test]
    public async Task K7_ExpiredAndBelowMinLevel_FallsBackToMissing_HardVetoEvenAtDefaultSettings()
    {
        // Deliberate deviation from the S3-1 level pinning: the anomaly only exists when the held
        // level is below MinLevel, so this night requirement demands Proficient.
        GivenRequirements(Requires(NightShiftId, NachtBefQualId, NachtBefName, minLevel: QualificationLevel.Proficient));
        GivenHeld(new List<ClientQualification>
        {
            Holds(Ma1, NachtBefQualId, validUntil: Ma2NachtBefValidUntil, level: QualificationLevel.Expert),
            Holds(Ma2, NachtBefQualId, validUntil: Ma2NachtBefValidUntil, level: QualificationLevel.Low),
        });

        var matrix = await _sut.BuildAsync(
            new[] { Ma1, Ma2 },
            new[] { new EligibilitySlot(NightShiftId, FirstExpiredNightDate) });

        var contrastKey = (Ma1.ToString(), NightShiftId, FirstExpiredNightDate);
        matrix.Ineligible.ShouldNotContain(
            contrastKey,
            "Finding K7 contrast: expired at sufficient level is classified Expired and stays a Warning at default settings - MA-1 remains assignable");
        matrix.Gaps[contrastKey].ShouldContain(
            g => g.Reason == QualificationGapReason.Expired && g.Severity == QualificationGapSeverity.Warning,
            "Finding K7 contrast: MA-1's gap is Expired/Warning");

        var anomalyKey = (Ma2.ToString(), NightShiftId, FirstExpiredNightDate);
        matrix.Ineligible.ShouldContain(
            anomalyKey,
            "Finding K7 (semantic anomaly, EligibilityMatcher.cs:92-103): a qualification that is expired AND below MinLevel falls through to Missing and becomes a hard veto even though QUALIFICATION_EXPIRED_MANDATORY_BLOCKS is unset - expiry blocks exactly when the level was too low, while expiry at a sufficient level only warns");
        matrix.Gaps[anomalyKey].ShouldContain(
            g => g.Reason == QualificationGapReason.Missing && g.Severity == QualificationGapSeverity.Error,
            "Finding K7: the anomaly surfaces as reason Missing (not Expired) with Error severity");
    }

    private void GivenRequirements(params ShiftRequiredQualification[] rows)
    {
        // Mirrors the exact-id filter of the production repository
        // (ShiftRequiredQualificationRepository.GetByShiftIdsAsync: Where(ids.Contains(srq.ShiftId))) -
        // a requirement on a shift id that is not among the slot shift ids is never returned.
        _shiftRepo.GetByShiftIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(ci => rows.Where(r => ci.Arg<IReadOnlyCollection<Guid>>().Contains(r.ShiftId)).ToList());
    }

    private void GivenHeld(IReadOnlyCollection<ClientQualification> rows)
    {
        _clientRepo.GetByClientIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(ci => rows.Where(r => ci.Arg<IReadOnlyCollection<Guid>>().Contains(r.ClientId)).ToList());
    }

    private void GivenExpiredMandatoryBlocksEnabled()
    {
        _settingsReader.GetSetting(SettingKeys.QualificationExpiredMandatoryBlocks)
            .Returns(new Klacks.Api.Domain.Models.Settings.Settings
            {
                Type = SettingKeys.QualificationExpiredMandatoryBlocks,
                Value = SettingEnabledValue,
            });
    }

    private Task<EligibilityMatrix> BuildAllSlotsAsync() => _sut.BuildAsync(AllAgents, AllSlots());

    private static ShiftRequiredQualification NightRequiresNachtBef()
        => Requires(NightShiftId, NachtBefQualId, NachtBefName);

    private static ShiftRequiredQualification Requires(
        Guid shiftId, Guid qualificationId, string qualificationName, QualificationLevel minLevel = PinnedMinLevel)
        => new()
        {
            ShiftId = shiftId,
            QualificationId = qualificationId,
            IsMandatory = true,
            MinLevel = minLevel,
            Qualification = new Qualification { Name = new MultiLanguage { De = qualificationName } },
        };

    private static ClientQualification Holds(
        Guid clientId, Guid qualificationId, DateOnly? validUntil = null, QualificationLevel level = PinnedHeldLevel)
        => new()
        {
            ClientId = clientId,
            QualificationId = qualificationId,
            Level = level,
            ValidUntil = validUntil,
        };

    private static List<ClientQualification> BaseHeldRows() => new()
    {
        Holds(Ma1, NachtBefQualId),
        Holds(Ma1, ObjektAQualId),
        Holds(Ma2, NachtBefQualId, validUntil: Ma2NachtBefValidUntil),
        Holds(Ma2, ObjektAQualId),
        Holds(Ma3, ObjektAQualId),
        Holds(Ma4, ObjektAQualId),
        Holds(Ma5, NachtBefQualId),
        Holds(Ma5, ObjektAQualId),
    };

    private static List<EligibilitySlot> AllSlots()
    {
        var slots = new List<EligibilitySlot>();
        for (var date = PeriodStart; date <= PeriodEnd; date = date.AddDays(1))
        {
            slots.Add(new EligibilitySlot(EarlyShiftId, date));
            slots.Add(new EligibilitySlot(LateShiftId, date));
            slots.Add(new EligibilitySlot(NightShiftId, date));
        }

        return slots;
    }

    private static List<(string AgentId, Guid ShiftId, DateOnly Date)> NightTriples(Guid agent, DateOnly from, DateOnly until)
    {
        var triples = new List<(string, Guid, DateOnly)>();
        for (var date = from; date <= until; date = date.AddDays(1))
        {
            triples.Add((agent.ToString(), NightShiftId, date));
        }

        return triples;
    }

    private static List<(string AgentId, Guid ShiftId, DateOnly Date)> DefaultRunExpectedTriples()
    {
        var expected = NightTriples(Ma3, PeriodStart, PeriodEnd);
        expected.AddRange(NightTriples(Ma4, PeriodStart, PeriodEnd));
        expected.Count.ShouldBe(ExpectedDefaultVetoCount);
        return expected;
    }

    private static List<(string AgentId, Guid ShiftId, DateOnly Date)> SettingRunExpectedTriples()
    {
        var expected = DefaultRunExpectedTriples();
        expected.AddRange(NightTriples(Ma2, FirstExpiredNightDate, PeriodEnd));
        expected.Count.ShouldBe(ExpectedSettingVetoCount);
        return expected;
    }

    private static List<(string AgentId, Guid ShiftId, DateOnly Date)> AllMissingExpectedTriples()
    {
        var expected = new List<(string AgentId, Guid ShiftId, DateOnly Date)>();
        foreach (var agent in AllAgents)
        {
            expected.AddRange(NightTriples(agent, PeriodStart, PeriodEnd));
        }

        expected.Count.ShouldBe(ExpectedAllMissingVetoCount);
        return expected;
    }

    private static HashSet<DateOnly> AllPeriodDates()
    {
        var dates = new HashSet<DateOnly>();
        for (var date = PeriodStart; date <= PeriodEnd; date = date.AddDays(1))
        {
            dates.Add(date);
        }

        return dates;
    }

    private static List<CoreShift> ScenarioCoreShifts()
    {
        var shifts = new List<CoreShift>();
        for (var date = PeriodStart; date <= PeriodEnd; date = date.AddDays(1))
        {
            var isoDate = Iso(date);
            shifts.Add(new CoreShift(EarlyShiftId.ToString(), EarlyShiftName, isoDate, EarlyStartTime, EarlyEndTime, ShiftDurationHours, SingleCoverage, DefaultPriority));
            shifts.Add(new CoreShift(LateShiftId.ToString(), LateShiftName, isoDate, LateStartTime, LateEndTime, ShiftDurationHours, SingleCoverage, DefaultPriority));
            shifts.Add(new CoreShift(NightShiftId.ToString(), NightShiftName, isoDate, NightStartTime, NightEndTime, ShiftDurationHours, SingleCoverage, DefaultPriority));
        }

        return shifts;
    }

    private static List<CoreAgent> AllCoreAgents()
        => AllAgents.Select(id => new CoreAgent(
            Id: id.ToString(),
            CurrentHours: 0,
            GuaranteedHours: 0,
            MaxConsecutiveDays: 0,
            MinRestHours: 0,
            Motivation: 0,
            MaxDailyHours: 0,
            MaxWeeklyHours: 0,
            MaxOptimalGap: 0)).ToList();

    private static string Iso(DateOnly date) => date.ToString(IsoDateFormat, CultureInfo.InvariantCulture);

    private static void ShouldBeExactlyTriples(
        IReadOnlySet<(string AgentId, Guid ShiftId, DateOnly Date)> actual,
        IReadOnlyCollection<(string AgentId, Guid ShiftId, DateOnly Date)> expected,
        string context)
    {
        var expectedSet = expected.ToHashSet();
        var missing = expectedSet.Except(actual).ToList();
        var extra = actual.Except(expectedSet).ToList();
        (missing.Count + extra.Count).ShouldBe(
            0,
            $"{context} - {missing.Count} expected triples absent (first: {(missing.Count > 0 ? missing[0] : default)}), {extra.Count} unexpected triples present (first: {(extra.Count > 0 ? extra[0] : default)})");
    }
}
