// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Globalization;
using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution.Auction.Agent;
using Klacks.ScheduleOptimizer.TokenEvolution.Auction.Conductor;
using Klacks.ScheduleOptimizer.TokenEvolution.Auction.Controller;
using Klacks.ScheduleOptimizer.Common.Fuzzy;
using Klacks.ScheduleOptimizer.TokenEvolution.Initialization;
using Klacks.UnitTest.Autofill.Fixtures;
using Klacks.UnitTest.Autofill.Support;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Autofill.Scenarios.Scenario4;

/// <summary>
/// Stage E1.0 of the scenario-4 fix plan: measures the bidding situation instead of deriving it.
/// The plan's leading finding — a carry-in package is not vetoed but outbid — was a hand calculation
/// over membership functions, and the whole carry-in design rests on it. This fixture runs the
/// production auction with a recording bidding agent, reproduces the private feature extraction and
/// proves the reproduction against the engine's own score, then prints the full bid table of the
/// first period day, the same table for continuation days inside the period, and the counterfactual
/// scores the rule base would produce without the two rejecting rules.
/// <para>
/// Stage E1 answered the measurement: the continuation is constructed before the free assignment and
/// its slots never reach the auction at all. The assertions therefore pin that consequence — no bid on
/// a constructed day — instead of the bid comparison, which is emergent and moves with every change to
/// the accumulated run hours. The bid comparison stays in the report because the bias it shows is
/// still real for packages that START inside the period, which is the subject of a later stage.
/// </para>
/// </summary>
[TestFixture]
[Category("Autofill")]
[Category("Scenario4")]
public class Scenario4BidDiagnosticsTests
{
    private const double ScoreTolerance = 1e-12;

    private const string SatedRuleName = "R11_SatedMidRun";

    private const string CoolRotationRuleName = "R22_SlotRotationCool";

    private const string FullRuleBaseLabel = "full rule base";

    private const string WithoutCoolRotationLabel = "without R22_SlotRotationCool";

    private const string WithoutSatedLabel = "without R11_SatedMidRun";

    private const string WithoutBothLabel = "without R11 and R22";

    private const string StarvingHighRuleName = "R01_StarvingHigh";

    private const string StarvingEmptyRuleName = "R27_StarvingEmpty";

    private const string WithoutStarvingLabel = "without R01 and R27";

    private const string WithoutFourLabel = "without R01, R11, R22 and R27";

    private const int AblationVariantCount = 6;

    private const string ScoreFormat = "0.0000";

    private const string ShareFormat = "0.0%";

    private const string ActivationFormat = "0.00";

    private const string RuleSeparator = " | ";

    private const string ListSeparator = ", ";

    private const string None = "none";

    private const string Yes = "yes";

    private const string No = "no";

    private const int ShiftKindCount = 3;

    private const int NeverWorkedMarker = int.MaxValue;

    private const int SampleRowCount = 20;

    private static readonly double AcceptanceThreshold = new SlotAuctioneer(
        new FuzzyBiddingAgent(),
        new Stage0HardConstraintChecker(),
        new Stage1SoftConstraintChecker()).AcceptanceThreshold;

    private AutofillScenarioDefinition _definition = null!;

    private AuctionProbeResult _baseline = null!;

    [OneTimeSetUp]
    public void BuildTheFixtureAndRunTheAuctionOnce()
    {
        _definition = Scenario4CarryInFixture.BuildMainRun();
        _baseline = AuctionProbe.Run(
            _definition.Context,
            _definition.Config.RandomSeed,
            FullRuleBaseLabel,
            AuctionProbe.NoSuppressedRules);
    }

    /// <summary>
    /// Proves the feature replica against the engine. Without this every number in the other reports
    /// would be an unverified reconstruction of a private method.
    /// </summary>
    [Test]
    public void TheFeatureReplicaReproducesEveryEngineScore()
    {
        var engine = new MamdaniInferenceEngine(
            DefaultLinguisticVariables.BuildInputs(),
            DefaultLinguisticVariables.BuildOutput(),
            RuleBaseLoader.LoadDefault());

        var worstDeviation = 0.0;
        RecordedBid? worst = null;

        foreach (var record in _baseline.Records)
        {
            var features = BidFeatureReplica.Extract(
                record.Agent, record.Slot, record.State, _definition.Context);
            var replica = Math.Clamp(engine.Infer(features).CrispOutput, 0.0, 1.0);
            var deviation = Math.Abs(replica - record.Bid.Score);
            if (deviation > worstDeviation)
            {
                worstDeviation = deviation;
                worst = record;
            }
        }

        TestContext.Out.WriteLine(
            $"recorded bid calls: {_baseline.Records.Count.ToString(CultureInfo.InvariantCulture)}");
        TestContext.Out.WriteLine(
            "largest deviation replica vs engine: "
            + worstDeviation.ToString("0.0e+0", CultureInfo.InvariantCulture)
            + (worst is null ? string.Empty : $" at {worst.Agent.Id} {worst.SlotKey}"));

        worstDeviation.ShouldBeLessThan(ScoreTolerance);
    }

    /// <summary>
    /// Prints, for every one of the nine slots of the first period day, the full bid table, and states
    /// for each open carry-in package whether its slot went to auction at all and who ended up holding
    /// it. After stage E1 the nine package slots are constructed, so they carry no bid and no auction
    /// result — that is what this fixture pins.
    /// </summary>
    [Test]
    public void TheFirstDayPackageSlotsAreConstructedAndNeverAuctioned()
    {
        var day = AutofillSpecConstants.PeriodFrom;
        ReportContextHeader();

        foreach (var slot in SlotsOf(day))
        {
            ReportSlot(_baseline, slot);
        }

        var open = OpenPackages();
        var auctioned = new List<string>();
        var heldByOther = new List<string>();

        TestContext.Out.WriteLine(string.Empty);
        TestContext.Out.WriteLine("--- open carry-in packages on the first period day ---");
        TestContext.Out.WriteLine("agent | package slot | went to auction | bids recorded | holder in the seed plan");

        foreach (var carryIn in open)
        {
            var packageSlotId = AutofillShiftCatalog
                .ShiftIdOf(carryIn.OrderIndex, carryIn.ExpectedFirstShiftKind).ToString();
            var packageSlot = SlotsOf(day).Single(candidate => candidate.Id == packageSlotId);
            var ownKey = KeyOf(packageSlot);
            var bidCount = _baseline.Records.Count(record => record.SlotKey == ownKey);
            var wentToAuction = _baseline.Outcome.Results.Any(result => result.SlotId == ownKey);
            var holder = HolderOf(_baseline, day, packageSlot.Id);

            TestContext.Out.WriteLine(
                carryIn.AgentId
                + RuleSeparator + packageSlot.Name
                + RuleSeparator + (wentToAuction ? Yes : No)
                + RuleSeparator + bidCount.ToString(CultureInfo.InvariantCulture)
                + RuleSeparator + holder);

            if (wentToAuction || bidCount > 0)
            {
                auctioned.Add(carryIn.AgentId);
            }

            if (holder != carryIn.AgentId)
            {
                heldByOther.Add(carryIn.AgentId + " -> " + holder);
            }
        }

        TestContext.Out.WriteLine(string.Empty);
        TestContext.Out.WriteLine(
            "verdict E1: package slots offered for sale = " + Describe(auctioned)
            + "; package slots held by a foreign employee = " + Describe(heldByOther));

        ReportKindPreferenceOfTheFirstDay(open, day);

        auctioned.ShouldBeEmpty(
            "a package slot the pre-pass already constructed must not be offered for sale a second time — that is "
            + "the occupancy skip of the auction loop, and without it the slot would end up double-staffed");
        heldByOther.ShouldBeEmpty(
            "every open package must be held on its own order and kind by its own employee on the first period day; "
            + "B-0.1 showed the auction gives it away, so the construction now decides it");
    }

    /// <summary>
    /// The follow-up finding: after a win the state reports zero days since the agent's own shift
    /// kind, so on the next day the continuation slot reads Cool while every kind the agent never
    /// worked reads Hot. Measured on identical state — the compared bids differ in nothing but the
    /// slot's shift kind, because they are all taken before the agent won anything that day. The bias
    /// is still present and still shapes packages that start inside the period; what the assertion
    /// pins is that the packages carried in from the previous month no longer depend on it, because
    /// not one of their owed days is auctioned any more.
    /// </summary>
    [Test]
    public void NoOwedCarryInDayIsAuctionedWhileTheKindBiasIsStillReported()
    {
        var comparisons = ContinuationComparisons().ToList();
        var continuationLower = comparisons.Count(c => c.Continuation.Bid.Score < c.Switch.Bid.Score);
        var continuationEqual = comparisons.Count(c =>
            Math.Abs(c.Continuation.Bid.Score - c.Switch.Bid.Score) <= ScoreTolerance);
        var continuationHigher = comparisons.Count(c =>
            c.Continuation.Bid.Score - c.Switch.Bid.Score > ScoreTolerance);
        var continuationBelowThreshold = comparisons.Count(c => c.Continuation.Bid.Score < AcceptanceThreshold);
        var switchBelowThreshold = comparisons.Count(c => c.Switch.Bid.Score < AcceptanceThreshold);
        var continuationWon = comparisons.Count(c =>
            _baseline.WinnerBySlot.GetValueOrDefault(c.Continuation.SlotKey, None) == c.Continuation.Agent.Id);

        TestContext.Out.WriteLine(
            "--- same agent, same day, same state: own shift kind against the best other kind ---");
        TestContext.Out.WriteLine(
            "comparable situations: " + comparisons.Count.ToString(CultureInfo.InvariantCulture));
        TestContext.Out.WriteLine(
            "own kind scores lower: " + Share(continuationLower, comparisons.Count));
        TestContext.Out.WriteLine(
            "own kind scores equal: " + Share(continuationEqual, comparisons.Count));
        TestContext.Out.WriteLine(
            "own kind scores higher: " + Share(continuationHigher, comparisons.Count));
        TestContext.Out.WriteLine(
            "own kind below the acceptance threshold "
            + Format(AcceptanceThreshold) + ": " + Share(continuationBelowThreshold, comparisons.Count));
        TestContext.Out.WriteLine(
            "other kind below the acceptance threshold: " + Share(switchBelowThreshold, comparisons.Count));
        TestContext.Out.WriteLine(
            "agent still won its own-kind slot: " + Share(continuationWon, comparisons.Count));

        TestContext.Out.WriteLine(string.Empty);
        TestContext.Out.WriteLine("--- situations in which the own kind did NOT score lower ---");
        foreach (var comparison in comparisons
            .Where(c => c.Continuation.Bid.Score >= c.Switch.Bid.Score - ScoreTolerance))
        {
            TestContext.Out.WriteLine(
                comparison.Continuation.Date.ToString(
                    AutofillSpecConstants.IsoDateFormat, CultureInfo.InvariantCulture)
                + RuleSeparator + comparison.Continuation.Agent.Id
                + RuleSeparator + Format(comparison.Continuation.Bid.Score)
                + RuleSeparator + Format(comparison.Switch.Bid.Score)
                + " (" + comparison.Switch.Slot.Name + ")");
        }

        TestContext.Out.WriteLine(string.Empty);
        TestContext.Out.WriteLine("day | agent | block | own kind score | best other kind score | fired on own kind");
        foreach (var comparison in comparisons.Take(SampleRowCount))
        {
            TestContext.Out.WriteLine(
                comparison.Continuation.Date.ToString(AutofillSpecConstants.IsoDateFormat, CultureInfo.InvariantCulture)
                + RuleSeparator + comparison.Continuation.Agent.Id
                + RuleSeparator + comparison.Continuation.State.CurrentBlockLength.ToString(CultureInfo.InvariantCulture)
                + RuleSeparator + Format(comparison.Continuation.Bid.Score)
                + RuleSeparator + Format(comparison.Switch.Bid.Score)
                + " (" + comparison.Switch.Slot.Name + ")"
                + RuleSeparator + string.Join(ListSeparator, comparison.Continuation.Bid.FiredRules));
        }

        var owed = OwedContinuationSlots().ToList();
        var auctionedOwedDays = owed
            .Where(entry => _baseline.Records.Any(record => record.SlotKey == entry.SlotKey))
            .Select(entry => entry.AgentId + " " + entry.SlotKey)
            .ToList();

        TestContext.Out.WriteLine(string.Empty);
        TestContext.Out.WriteLine(
            "owed carry-in days: " + owed.Count.ToString(CultureInfo.InvariantCulture)
            + ", of which auctioned: " + auctionedOwedDays.Count.ToString(CultureInfo.InvariantCulture));

        comparisons.ShouldNotBeEmpty(
            "the report needs comparable situations; an empty set would mean the recorder saw no consecutive day at "
            + "all and the kind bias could not be judged either way");
        owed.ShouldNotBeEmpty(
            "the fixture must owe carry-in days, otherwise this fixture proves nothing about the construction");
        auctionedOwedDays.ShouldBeEmpty(
            "not one day still owed by a carried-in package may be offered for sale: the pre-pass constructs them "
            + "all and the occupancy skip keeps the auction away from them. This replaces the two pins of stage "
            + "E1.0 — the bid comparison above is emergent and moves with the accumulated run hours, while the "
            + "absence of a bid on a constructed day is the direct consequence of the fix: "
            + string.Join(ListSeparator, auctionedOwedDays));
    }

    /// <summary>Every (employee, slot) an open package still owes, in day-major order.</summary>
    private IEnumerable<(string AgentId, string SlotKey)> OwedContinuationSlots()
    {
        foreach (var carryIn in OpenPackages())
        {
            var shiftId = AutofillShiftCatalog.ShiftIdOf(carryIn.OrderIndex, carryIn.ExpectedFirstShiftKind);
            for (var offset = 0; offset < carryIn.MissingDays; offset++)
            {
                var day = AutofillSpecConstants.PeriodFrom.AddDays(offset);
                yield return (
                    carryIn.AgentId,
                    day.ToString(AutofillSpecConstants.IsoDateFormat, CultureInfo.InvariantCulture)
                        + RecordedBid.SlotKeySeparator + shiftId);
            }
        }
    }

    /// <summary>Employee holding the slot in the probe's seed plan, or a marker when nobody does.</summary>
    private static string HolderOf(AuctionProbeResult probe, DateOnly day, string shiftId)
    {
        var holders = probe.Outcome.Scenario.Tokens
            .Where(token => token.Date == day && token.ShiftRefId.ToString() == shiftId)
            .Select(token => token.AgentId)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        return holders.Count == 0 ? None : string.Join(ListSeparator, holders);
    }

    /// <summary>
    /// Counterfactual for the planned rule-base change: the same auction with single rules removed.
    /// The plan proposes to gate <c>R22_SlotRotationCool</c> on an empty block; this measures whether
    /// that alone changes anything, given that <c>R11_SatedMidRun</c> rejects on block hunger alone.
    /// </summary>
    [Test]
    public void TheRuleAblationShowsWhichRejectingRuleActuallyDecides()
    {
        var variants = new[]
        {
            _baseline,
            RunVariant(WithoutCoolRotationLabel, [CoolRotationRuleName]),
            RunVariant(WithoutSatedLabel, [SatedRuleName]),
            RunVariant(WithoutBothLabel, [SatedRuleName, CoolRotationRuleName]),
            RunVariant(WithoutStarvingLabel, [StarvingHighRuleName, StarvingEmptyRuleName]),
            RunVariant(
                WithoutFourLabel,
                [StarvingHighRuleName, StarvingEmptyRuleName, SatedRuleName, CoolRotationRuleName]),
        };

        var day = AutofillSpecConstants.PeriodFrom;
        var open = OpenPackages();

        TestContext.Out.WriteLine(
            "variant | continued packages on the first day | same-kind day transitions | tokens in the seed plan");
        foreach (var variant in variants)
        {
            var continued = open.Count(carryIn => HolderOf(
                variant,
                day,
                AutofillShiftCatalog.ShiftIdOf(carryIn.OrderIndex, carryIn.ExpectedFirstShiftKind).ToString())
                    == carryIn.AgentId);

            var continuity = MeasureKindContinuity(variant.Outcome.Scenario);

            TestContext.Out.WriteLine(
                variant.Label
                + RuleSeparator + continued.ToString(CultureInfo.InvariantCulture)
                + RuleSeparator + Share(continuity.SameKind, continuity.Consecutive)
                + RuleSeparator + variant.Outcome.Scenario.Tokens.Count.ToString(CultureInfo.InvariantCulture));
        }

        TestContext.Out.WriteLine(string.Empty);
        TestContext.Out.WriteLine("--- own-slot score of every open package under each variant ---");
        TestContext.Out.WriteLine("agent | " + string.Join(RuleSeparator, variants.Select(v => v.Label)));

        foreach (var carryIn in open)
        {
            var key = day.ToString(AutofillSpecConstants.IsoDateFormat, CultureInfo.InvariantCulture)
                + RecordedBid.SlotKeySeparator
                + AutofillShiftCatalog.ShiftIdOf(carryIn.OrderIndex, carryIn.ExpectedFirstShiftKind);

            TestContext.Out.WriteLine(
                carryIn.AgentId
                + RuleSeparator
                + string.Join(RuleSeparator, variants.Select(variant =>
                {
                    var bid = BidOf(variant, key, carryIn.AgentId);
                    return bid is null ? None : Format(bid.Score);
                })));
        }

        variants.Length.ShouldBe(AblationVariantCount);
    }

    /// <summary>
    /// Two state properties the plan does not describe: how <c>DaysSinceShiftType</c> develops when
    /// the agent rests — it is advanced per assignment, not per calendar day — and how
    /// <c>WeeklyLoad</c> behaves once the accumulated hours of the whole run leave the domain of the
    /// weekly membership functions.
    /// </summary>
    [Test]
    public void TheRuntimeStateEvolutionIsReported()
    {
        var probeAgent = _definition.Context.Agents[0].Id;
        var perDay = _baseline.Records
            .Where(record => record.Agent.Id == probeAgent)
            .GroupBy(record => record.Date)
            .OrderBy(group => group.Key)
            .Select(group => group.First())
            .ToList();

        TestContext.Out.WriteLine("--- runtime state of " + probeAgent + ", first bid of each day ---");
        TestContext.Out.WriteLine("day | lastWorked | blockLen | daysSince E/L/N | hoursThisRun | weeklyLoad");
        foreach (var record in perDay)
        {
            var features = BidFeatureReplica.Extract(
                record.Agent, record.Slot, record.State, _definition.Context);
            TestContext.Out.WriteLine(
                record.Date.ToString(AutofillSpecConstants.IsoDateFormat, CultureInfo.InvariantCulture)
                + RuleSeparator + (record.State.LastWorkedDate?.ToString(
                    AutofillSpecConstants.IsoDateFormat, CultureInfo.InvariantCulture) ?? None)
                + RuleSeparator + record.State.CurrentBlockLength.ToString(CultureInfo.InvariantCulture)
                + RuleSeparator + DescribeDaysSince(record.State)
                + RuleSeparator + record.State.HoursAssignedThisRun.ToString(
                    ScoreFormat, CultureInfo.InvariantCulture)
                + RuleSeparator + BidFeatureReplica.Describe(
                    BidFeatureReplica.WeeklyLoad, features[BidFeatureReplica.WeeklyLoad]));
        }

        var variables = DefaultLinguisticVariables.BuildInputs();
        var weeklyLoad = variables[BidFeatureReplica.WeeklyLoad];
        var silentWeeklyLoad = _baseline.Records.Count(record =>
        {
            var value = BidFeatureReplica.Extract(
                record.Agent, record.Slot, record.State, _definition.Context)[BidFeatureReplica.WeeklyLoad];
            return weeklyLoad.TermNames.All(term => weeklyLoad.Mu(term, value) <= 0);
        });

        TestContext.Out.WriteLine(string.Empty);
        TestContext.Out.WriteLine(
            "bids whose WeeklyLoad lies outside every membership function (all WeeklyLoad rules silent): "
            + Share(silentWeeklyLoad, _baseline.Records.Count));

        var restingDays = _baseline.Records.Count(record =>
            record.State.LastWorkedDate.HasValue
            && record.Date.DayNumber - record.State.LastWorkedDate.Value.DayNumber > 1
            && record.State.DaysSinceShiftType.Any(days => days == 0));

        TestContext.Out.WriteLine(
            "bids after at least one rest day that still report zero days since a shift kind: "
            + Share(restingDays, _baseline.Records.Count));

        perDay.ShouldNotBeEmpty();
    }

    private AuctionProbeResult RunVariant(string label, IReadOnlyCollection<string> suppressed)
        => AuctionProbe.Run(_definition.Context, _definition.Config.RandomSeed, label, suppressed);

    private IReadOnlyList<AutofillCarryIn> OpenPackages()
        => [.. _definition.CarryIns.Where(carryIn => carryIn.IsOpenAt(AutofillSpecConstants.PeriodFrom))];

    private IReadOnlyList<CoreShift> SlotsOf(DateOnly day)
    {
        var iso = day.ToString(AutofillSpecConstants.IsoDateFormat, CultureInfo.InvariantCulture);
        return [.. _definition.Context.Shifts
            .Where(shift => shift.Date == iso)
            .OrderBy(shift => shift.StartTime, StringComparer.Ordinal)
            .ThenBy(shift => shift.Id, StringComparer.Ordinal)];
    }

    private void ReportContextHeader()
    {
        var first = _definition.Context.Agents[0];
        TestContext.Out.WriteLine(
            "scenario 4a: "
            + _definition.Context.Agents.Count.ToString(CultureInfo.InvariantCulture) + " agents, "
            + _definition.Context.Shifts.Count.ToString(CultureInfo.InvariantCulture) + " slots, "
            + _definition.Context.BoundaryLockedWorks.Count.ToString(CultureInfo.InvariantCulture)
            + " boundary works, currentHours " + first.CurrentHours.ToString(CultureInfo.InvariantCulture)
            + ", guaranteedHours " + first.GuaranteedHours.ToString(CultureInfo.InvariantCulture)
            + ", maxWeeklyHours " + first.MaxWeeklyHours.ToString(CultureInfo.InvariantCulture)
            + ", carry-in hours counted as currentHours: "
            + (_definition.CarryInHoursCountedAsCurrentHours ? Yes : No));
        TestContext.Out.WriteLine(
            "acceptance threshold: " + Format(AcceptanceThreshold));
        TestContext.Out.WriteLine(
            "auction order inside one day: "
            + AutofillShiftCatalog.DescribeAuctionOrderOfOneDay(_definition.OrderCount));
    }

    private void ReportSlot(AuctionProbeResult probe, CoreShift slot)
    {
        var key = KeyOf(slot);
        var result = probe.Outcome.Results.SingleOrDefault(candidate => candidate.SlotId == key);
        var records = probe.Records.Where(record => record.SlotKey == key).ToList();
        if (result is null)
        {
            TestContext.Out.WriteLine(string.Empty);
            TestContext.Out.WriteLine(
                "=== " + slot.Date + " " + slot.Name + " " + slot.StartTime + "-" + slot.EndTime
                + " -> not auctioned, already staffed by the carry-in construction; holder "
                + HolderOf(probe, DateOnly.ParseExact(
                    slot.Date, AutofillSpecConstants.IsoDateFormat, CultureInfo.InvariantCulture), slot.Id)
                + " ===");
            return;
        }

        var bidders = records.Select(record => record.Agent.Id).ToHashSet(StringComparer.Ordinal);
        var vetoed = _definition.Context.Agents
            .Select(agent => agent.Id)
            .Where(id => !bidders.Contains(id))
            .ToList();

        TestContext.Out.WriteLine(string.Empty);
        TestContext.Out.WriteLine(
            "=== " + slot.Date + " " + slot.Name + " " + slot.StartTime + "-" + slot.EndTime
            + " -> winner " + (result.WinnerAgentId ?? None)
            + " (round " + result.Round.ToString(CultureInfo.InvariantCulture) + ") ===");
        TestContext.Out.WriteLine("stage-0 vetoed, no bid: " + Describe(vetoed));
        TestContext.Out.WriteLine(
            "agent | newBlock | hunger | maturity | slotDaysSince | weeklyLoad | indexBonus | score | fired");

        foreach (var record in records.OrderByDescending(record => record.Bid.Score))
        {
            var features = BidFeatureReplica.Extract(
                record.Agent, record.Slot, record.State, _definition.Context);
            TestContext.Out.WriteLine(
                record.Agent.Id
                + RuleSeparator + (BidFeatureReplica.StartsNewBlock(record.Slot, record.State) ? Yes : No)
                + RuleSeparator + BidFeatureReplica.Describe(
                    BidFeatureReplica.BlockHunger, features[BidFeatureReplica.BlockHunger])
                + RuleSeparator + BidFeatureReplica.Describe(
                    BidFeatureReplica.BlockMaturity, features[BidFeatureReplica.BlockMaturity])
                + RuleSeparator + BidFeatureReplica.Describe(
                    BidFeatureReplica.SlotDaysSince, features[BidFeatureReplica.SlotDaysSince])
                + RuleSeparator + BidFeatureReplica.Describe(
                    BidFeatureReplica.WeeklyLoad, features[BidFeatureReplica.WeeklyLoad])
                + RuleSeparator + BidFeatureReplica.Describe(
                    BidFeatureReplica.IndexBonus, features[BidFeatureReplica.IndexBonus])
                + RuleSeparator + Format(record.Bid.Score)
                + RuleSeparator + DescribeFiredRules(features));
        }
    }

    private void ReportKindPreferenceOfTheFirstDay(IReadOnlyList<AutofillCarryIn> open, DateOnly day)
    {
        TestContext.Out.WriteLine(string.Empty);
        TestContext.Out.WriteLine(
            "--- first day, per open package: score of its own kind against the other two kinds ---");
        TestContext.Out.WriteLine("agent | own kind | own score | other kind scores");

        foreach (var carryIn in open)
        {
            var ownKind = AutofillShiftCatalog.ShiftTypeIndexOf(carryIn.ExpectedFirstShiftKind);
            var byKind = SlotsOf(day)
                .Select(slot => (Slot: slot, Bid: BidOf(_baseline, KeyOf(slot), carryIn.AgentId)))
                .Where(entry => entry.Bid is not null)
                .GroupBy(entry => KindIndexOf(entry.Slot))
                .ToDictionary(group => group.Key, group => group.Max(entry => entry.Bid!.Score));

            TestContext.Out.WriteLine(
                carryIn.AgentId
                + RuleSeparator + carryIn.ExpectedFirstShiftKind
                + RuleSeparator + (byKind.TryGetValue(ownKind, out var ownScore) ? Format(ownScore) : None)
                + RuleSeparator + string.Join(ListSeparator, byKind
                    .Where(entry => entry.Key != ownKind)
                    .OrderBy(entry => entry.Key)
                    .Select(entry =>
                        AutofillShiftCatalog.FromShiftTypeIndex(entry.Key) + " " + Format(entry.Value))));
        }
    }

    private IEnumerable<(RecordedBid Continuation, RecordedBid Switch)> ContinuationComparisons()
    {
        foreach (var group in _baseline.Records
            .Where(record => record.State.LastWorkedDate == record.Date.AddDays(-1))
            .GroupBy(record => (record.Agent.Id, record.Date))
            .OrderBy(group => group.Key.Date)
            .ThenBy(group => group.Key.Id, StringComparer.Ordinal))
        {
            var workedYesterday = group.First().State.DaysSinceShiftType
                .Select((days, index) => (days, index))
                .Where(entry => entry.days == 0)
                .Select(entry => entry.index)
                .ToList();

            if (workedYesterday.Count != 1)
            {
                continue;
            }

            var continuation = group
                .Where(record => KindIndexOf(record.Slot) == workedYesterday[0])
                .OrderByDescending(record => record.Bid.Score)
                .FirstOrDefault();
            var alternative = group
                .Where(record => KindIndexOf(record.Slot) != workedYesterday[0])
                .OrderByDescending(record => record.Bid.Score)
                .FirstOrDefault();

            if (continuation is not null && alternative is not null)
            {
                yield return (continuation, alternative);
            }
        }
    }

    private static string DescribeFiredRules(IReadOnlyDictionary<string, double> features)
    {
        var engine = new MamdaniInferenceEngine(
            DefaultLinguisticVariables.BuildInputs(),
            DefaultLinguisticVariables.BuildOutput(),
            RuleBaseLoader.LoadDefault());

        return string.Join(ListSeparator, engine.Infer(features).FiredRules
            .OrderByDescending(rule => rule.Activation)
            .Select(rule => rule.RuleName + "=" + rule.ConsequentTerm + "@"
                + rule.Activation.ToString(ActivationFormat, CultureInfo.InvariantCulture)));
    }

    private static string DescribeDaysSince(AgentRuntimeState state)
        => string.Join("/", Enumerable.Range(0, ShiftKindCount).Select(index =>
            index >= state.DaysSinceShiftType.Count
                ? None
                : state.DaysSinceShiftType[index] == NeverWorkedMarker
                    ? "never"
                    : state.DaysSinceShiftType[index].ToString(CultureInfo.InvariantCulture)));

    private static (int Consecutive, int SameKind) MeasureKindContinuity(CoreScenario scenario)
    {
        var consecutive = 0;
        var sameKind = 0;

        foreach (var group in scenario.Tokens.GroupBy(token => token.AgentId, StringComparer.Ordinal))
        {
            var ordered = group.OrderBy(token => token.Date).ToList();
            for (var i = 1; i < ordered.Count; i++)
            {
                if (ordered[i].Date.DayNumber - ordered[i - 1].Date.DayNumber != 1)
                {
                    continue;
                }

                consecutive++;
                if (ordered[i].ShiftTypeIndex == ordered[i - 1].ShiftTypeIndex)
                {
                    sameKind++;
                }
            }
        }

        return (consecutive, sameKind);
    }

    private static Bid? BidOf(AuctionProbeResult probe, string slotKey, string agentId)
        => probe.Records
            .FirstOrDefault(record => record.SlotKey == slotKey && record.Agent.Id == agentId)?.Bid;

    private static int KindIndexOf(CoreShift slot)
        => ShiftTypeInference.FromSpanString(slot.StartTime, slot.EndTime);

    private static string KeyOf(CoreShift slot) => slot.Date + RecordedBid.SlotKeySeparator + slot.Id;

    private static string Format(double value) => value.ToString(ScoreFormat, CultureInfo.InvariantCulture);

    private static string Share(int part, int whole)
        => part.ToString(CultureInfo.InvariantCulture) + "/" + whole.ToString(CultureInfo.InvariantCulture)
            + (whole == 0
                ? string.Empty
                : " (" + ((double)part / whole).ToString(ShareFormat, CultureInfo.InvariantCulture) + ")");

    private static string Describe(IReadOnlyCollection<string> items)
        => items.Count == 0 ? None : string.Join(ListSeparator, items);
}
