// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Globalization;
using Klacks.ScheduleOptimizer.Common.Fuzzy;
using Klacks.ScheduleOptimizer.TokenEvolution.Auction.Agent;
using Klacks.ScheduleOptimizer.TokenEvolution.Auction.Conductor;
using Klacks.ScheduleOptimizer.TokenEvolution.Auction.Controller;
using Klacks.UnitTest.Autofill.Support;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Autofill.Scenarios.Scenario4;

/// <summary>
/// Pre-computation for stage E4c, to be read before the first line of E4c code is written. The plan
/// carries two hand calculations that decide the whole rule-base change and neither was ever run
/// through the production inference engine: (a) whether a simultaneous <c>Reject@1.0</c> and
/// <c>High@1.0</c> defuzzifies above the acceptance threshold, so the proposed package advocate can
/// outweigh the saturation reject at all; and (b) whether <c>R16_BottomIndexSated</c> saturates the
/// bottom roster ranks exactly like <c>R11_SatedMidRun</c>, in which case R16 has to be gated in the
/// same commit as R11 instead of being left alone.
/// <para>
/// Both questions are answered against the shipped rule base and the shipped engine, never against a
/// modified <c>default-rules.json</c>: the variants are built in memory for this run only, the same
/// way <c>AuctionProbe</c> suppresses rules for its ablation. No scenario is planned and no plan is
/// asserted — the fixture computes membership arithmetic and prints it.
/// </para>
/// </summary>
[TestFixture]
[Explicit("E4c pre-computation; rule-base arithmetic only. Select it by name.")]
[Category("Autofill")]
[Category("Scenario4")]
public class Scenario4E4cRuleBaseDiagnosticsTests
{
    /// <summary>Block hunger at an empty block; mirrors <c>FuzzyBiddingAgent.cs:53-54</c>.</summary>
    private const double BlockSaturationHours = 36.0;

    /// <summary>Hours one package day consumes of the block hunger; mirrors <c>FuzzyBiddingAgent.cs:53-54</c>.</summary>
    private const double HoursPerBlockDay = 8.0;

    /// <summary>Roster size of scenario 4; the index bonus is a share of it.</summary>
    private const int RosterSize = Scenario4SpecConstants.EmployeeCount;

    /// <summary>Package length the plan's finding 1b is stated for: the bid on the fifth day.</summary>
    private const int DecisiveBlockLength = 4;

    private const string DaysSinceEarly = "DaysSinceEarly";

    private const string DaysSinceLate = "DaysSinceLate";

    private const string DaysSinceNight = "DaysSinceNight";

    /// <summary>Feature the plan proposes in E4c step 1; exists in no shipped variable set.</summary>
    private const string PackageTypeMatch = "PackageTypeMatch";

    private const string SatedRuleName = "R11_SatedMidRun";

    private const string BottomIndexSatedRuleName = "R16_BottomIndexSated";

    private const string CoolRotationRuleName = "R22_SlotRotationCool";

    private const string PackageHoldRuleName = "R41_PackageHold";

    private const string PackageBreakRuleName = "R42_PackageBreak";

    private const string BlockMaturityVariable = "BlockMaturity";

    private const string MidRunTerm = "MidRun";

    private const string BidScoreVariable = "BidScore";

    private const string RejectTerm = "Reject";

    private const string LowTerm = "Low";

    private const string MediumTerm = "Medium";

    private const string HighTerm = "High";

    private const string MustHaveTerm = "MustHave";

    private const string HoldTerm = "Hold";

    private const string NewBlockTerm = "NewBlock";

    private const string BreakTerm = "Break";

    private const string AndOperator = "AND";

    private const string ProbeVariable = "ProbeAlwaysOn";

    private const string ProbeTerm = "On";

    private const string ProbeRuleName = "ProbeRule";

    private const double ProbeCrisp = 1.0;

    /// <summary>Slot continues the running package with its own kind; the situation R41 is written for.</summary>
    private const double PackageTypeMatchHold = 1.0;

    private const string TodayLabel = "as shipped";

    private const string SatedGatedLabel = "R11 + BlockMaturity:MidRun";

    private const string BothGatedLabel = "R11 and R16 + BlockMaturity:MidRun";

    private const string AdvocateLabel = "R11/R16 gated, R22 gated, R41 and R42 added";

    private const string ScoreFormat = "0.0000";

    private const string ActivationFormat = "0.00";

    private const string ValueFormat = "0.###";

    private const string RuleSeparator = " | ";

    private const string ListSeparator = ", ";

    private const string None = "none";

    private const string Yes = "yes";

    private const string No = "no";

    private const string Confirmed = "CONFIRMED";

    private const string Refuted = "REFUTED";

    /// <summary>Bottom roster ranks the plan's finding 1b names; one-based.</summary>
    private static readonly int[] BottomRanks = [13, 14, 15];

    /// <summary>One crisp value per term of <c>SlotDaysSince</c>: Cool, Warm, Hot.</summary>
    private static readonly double[] SlotDaysSinceProbes = [0.0, 4.0, 9.0];

    /// <summary>Light, the value measured in the run, Heavy and beyond the domain edge.</summary>
    private static readonly double[] WeeklyLoadProbes = [0.0, 0.48, 0.9, 1.2];

    /// <summary>Package lengths the reject profile is printed for.</summary>
    private static readonly int[] BlockLengthProfile = [0, 1, 2, 3, 4, 5, 6];

    private static readonly double AcceptanceThreshold = new SlotAuctioneer(
        new FuzzyBiddingAgent(),
        new Stage0HardConstraintChecker(),
        new Stage1SoftConstraintChecker()).AcceptanceThreshold;

    /// <summary>
    /// Question (a) of the plan: the centroid of a simultaneous <c>Reject@1.0</c> and <c>High@1.0</c>.
    /// The plan computes it by hand as approximately 0.47 and warns that the number is calculated, not
    /// measured. It is measured here with the production defuzzifier, by handing a two-rule rule base
    /// an input that fires both rules at full strength — no replica of
    /// <c>MamdaniInferenceEngine.Defuzzify:82-107</c> is involved.
    /// </summary>
    [Test]
    public void TheCentroidOfASimultaneousRejectAndHighIsMeasuredNotDerived()
    {
        var combinations = new[]
        {
            new[] { RejectTerm },
            new[] { HighTerm },
            new[] { RejectTerm, HighTerm },
            new[] { RejectTerm, RejectTerm, HighTerm },
            new[] { RejectTerm, MediumTerm },
            new[] { RejectTerm, LowTerm },
            new[] { RejectTerm, MustHaveTerm },
        };

        TestContext.Out.WriteLine(
            "acceptance threshold " + Format(AcceptanceThreshold)
            + "; every term below is activated at 1.0");
        TestContext.Out.WriteLine("activated output terms | centroid | clears the threshold");

        var rejectAndHigh = 0.0;
        foreach (var terms in combinations)
        {
            var centroid = CentroidOf(terms);
            if (terms.Length == 2 && terms[0] == RejectTerm && terms[1] == HighTerm)
            {
                rejectAndHigh = centroid;
            }

            TestContext.Out.WriteLine(
                string.Join(" + ", terms)
                + RuleSeparator + Format(centroid)
                + RuleSeparator + (centroid >= AcceptanceThreshold ? Yes : No));
        }

        TestContext.Out.WriteLine(string.Empty);
        TestContext.Out.WriteLine(
            "Reject + High = " + Format(rejectAndHigh)
            + "; the plan's hand calculation says approximately 0.47.");
        TestContext.Out.WriteLine(
            "Aggregation is max per output term, so a second Reject at the same activation cannot move the "
            + "centroid — the row 'Reject + Reject + High' proves that and it means the advocate has to "
            + "outweigh ONE reject shape no matter how many reject rules fire.");

        combinations.Length.ShouldBeGreaterThan(0);
    }

    /// <summary>
    /// Question (b) of the plan: which rules actually fire for a bottom-ranked employee bidding on the
    /// fifth day of its own package, and whether gating <c>R11</c> alone changes that. The sweep varies
    /// everything the situation leaves free — rotation distance, weekly load, roster rank — so the
    /// answer is not the property of one invented feature vector.
    /// </summary>
    [Test]
    public void TheBottomIndexSweepAtBlockLengthFourListsEveryFiringRule()
    {
        var variants = BuildVariants();
        var rows = new List<SweepRow>();

        foreach (var variant in variants)
        {
            foreach (var rank in BottomRanks)
            {
                foreach (var daysSince in SlotDaysSinceProbes)
                {
                    foreach (var weeklyLoad in WeeklyLoadProbes)
                    {
                        rows.Add(Evaluate(variant, rank, DecisiveBlockLength, daysSince, weeklyLoad));
                    }
                }
            }
        }

        ReportMembershipContext();
        ReportSweep(rows);
        ReportBlockLengthProfile(variants);
        ReportVerdicts(rows, variants);

        rows.ShouldNotBeEmpty(
            "the sweep must produce rows; an empty sweep would let the verdict below read as a finding");
    }

    private static IReadOnlyList<RuleVariant> BuildVariants()
    {
        var shipped = RuleBaseLoader.LoadDefault();
        var satedGated = WithExtraAntecedent(shipped, SatedRuleName, BlockMaturityVariable, MidRunTerm);
        var bothGated = WithExtraAntecedent(satedGated, BottomIndexSatedRuleName, BlockMaturityVariable, MidRunTerm);
        var advocate = WithExtraAntecedent(bothGated, CoolRotationRuleName, PackageTypeMatch, NewBlockTerm)
            .Append(new FuzzyRule(
                PackageHoldRuleName,
                [new RuleClause(PackageTypeMatch, HoldTerm)],
                AndOperator,
                BidScoreVariable,
                HighTerm))
            .Append(new FuzzyRule(
                PackageBreakRuleName,
                [new RuleClause(PackageTypeMatch, BreakTerm)],
                AndOperator,
                BidScoreVariable,
                RejectTerm))
            .ToList();

        return
        [
            new RuleVariant(TodayLabel, shipped),
            new RuleVariant(SatedGatedLabel, satedGated),
            new RuleVariant(BothGatedLabel, bothGated),
            new RuleVariant(AdvocateLabel, advocate),
        ];
    }

    private static SweepRow Evaluate(
        RuleVariant variant, int rank, int blockLength, double slotDaysSince, double weeklyLoad)
    {
        var engine = new MamdaniInferenceEngine(
            BuildInputs(), DefaultLinguisticVariables.BuildOutput(), variant.Rules);
        var features = BuildFeatures(rank, blockLength, slotDaysSince, weeklyLoad);
        var result = engine.Infer(features);
        var score = Math.Clamp(result.CrispOutput, 0.0, 1.0);
        var reject = result.FiredRules
            .Where(rule => rule.ConsequentTerm == RejectTerm)
            .Select(rule => rule.Activation)
            .DefaultIfEmpty(0.0)
            .Max();

        return new SweepRow(
            variant.Label, rank, blockLength, slotDaysSince, weeklyLoad, score, reject, result.FiredRules);
    }

    /// <summary>
    /// Crisp feature vector of a bottom-ranked employee mid-package. The three per-kind rotation
    /// features are set to the swept slot distance, so the sweep also covers the two rules that read
    /// them; the report shows whether they can fire here at all.
    /// </summary>
    private static Dictionary<string, double> BuildFeatures(
        int rank, int blockLength, double slotDaysSince, double weeklyLoad)
        => new(StringComparer.Ordinal)
        {
            [BidFeatureReplica.BlockHunger] = Math.Max(0, BlockSaturationHours - (blockLength * HoursPerBlockDay)),
            [BidFeatureReplica.BlockMaturity] = blockLength,
            [DaysSinceEarly] = slotDaysSince,
            [DaysSinceLate] = slotDaysSince,
            [DaysSinceNight] = slotDaysSince,
            [BidFeatureReplica.SlotDaysSince] = slotDaysSince,
            [BidFeatureReplica.WeeklyLoad] = weeklyLoad,
            [BidFeatureReplica.IndexBonus] = IndexBonusOf(rank),
            [BidFeatureReplica.NewBlockSameType] = 0.0,
            [PackageTypeMatch] = PackageTypeMatchHold,
        };

    /// <summary>
    /// Shipped input variables plus the one the plan proposes, with the membership functions the plan
    /// names in E4c step 1. Adding it to the dictionary is harmless for the shipped rule base: no
    /// shipped rule reads it.
    /// </summary>
    private static IReadOnlyDictionary<string, LinguisticVariable> BuildInputs()
    {
        var inputs = new Dictionary<string, LinguisticVariable>(
            DefaultLinguisticVariables.BuildInputs(), StringComparer.Ordinal)
        {
            [PackageTypeMatch] = new LinguisticVariable(
                PackageTypeMatch,
                new Dictionary<string, MembershipFunction>(StringComparer.Ordinal)
                {
                    [BreakTerm] = new TrapezoidMf(0, 0, 0.15, 0.35),
                    [NewBlockTerm] = new TriangularMf(0.3, 0.5, 0.7),
                    [HoldTerm] = new TrapezoidMf(0.65, 0.85, 1, 1),
                }),
        };

        return inputs;
    }

    private static IReadOnlyList<FuzzyRule> WithExtraAntecedent(
        IReadOnlyList<FuzzyRule> rules, string ruleName, string variable, string term)
        => [.. rules.Select(rule => rule.Name == ruleName
            ? rule with { Antecedents = [.. rule.Antecedents, new RuleClause(variable, term)] }
            : rule)];

    private static double CentroidOf(IReadOnlyList<string> outputTerms)
    {
        var inputs = new Dictionary<string, LinguisticVariable>(StringComparer.Ordinal)
        {
            [ProbeVariable] = new LinguisticVariable(
                ProbeVariable,
                new Dictionary<string, MembershipFunction>(StringComparer.Ordinal)
                {
                    [ProbeTerm] = new TrapezoidMf(0, 0, 1, 1),
                }),
        };

        var rules = new List<FuzzyRule>(outputTerms.Count);
        for (var i = 0; i < outputTerms.Count; i++)
        {
            rules.Add(new FuzzyRule(
                ProbeRuleName + i.ToString(CultureInfo.InvariantCulture),
                [new RuleClause(ProbeVariable, ProbeTerm)],
                AndOperator,
                BidScoreVariable,
                outputTerms[i]));
        }

        var engine = new MamdaniInferenceEngine(inputs, DefaultLinguisticVariables.BuildOutput(), rules);
        var crisp = new Dictionary<string, double>(StringComparer.Ordinal) { [ProbeVariable] = ProbeCrisp };
        return engine.Infer(crisp).CrispOutput;
    }

    private static double IndexBonusOf(int rank) => (rank - 1) / (double)(RosterSize - 1);

    private static void ReportMembershipContext()
    {
        var variables = DefaultLinguisticVariables.BuildInputs();
        var hunger = Math.Max(0, BlockSaturationHours - (DecisiveBlockLength * HoursPerBlockDay));

        TestContext.Out.WriteLine(
            "block length " + DecisiveBlockLength.ToString(CultureInfo.InvariantCulture)
            + " -> BlockHunger " + BidFeatureReplica.Describe(BidFeatureReplica.BlockHunger, hunger)
            + ", BlockMaturity "
            + BidFeatureReplica.Describe(BidFeatureReplica.BlockMaturity, DecisiveBlockLength));

        foreach (var rank in BottomRanks)
        {
            TestContext.Out.WriteLine(
                "rank " + rank.ToString(CultureInfo.InvariantCulture)
                + " of " + RosterSize.ToString(CultureInfo.InvariantCulture)
                + " -> IndexBonus "
                + BidFeatureReplica.Describe(BidFeatureReplica.IndexBonus, IndexBonusOf(rank)));
        }

        TestContext.Out.WriteLine(
            "acceptance threshold " + Format(AcceptanceThreshold)
            + "; WeeklyLoad terms of the shipped set: "
            + string.Join(ListSeparator, variables[BidFeatureReplica.WeeklyLoad].TermNames));
    }

    private static void ReportSweep(IReadOnlyList<SweepRow> rows)
    {
        TestContext.Out.WriteLine(string.Empty);
        TestContext.Out.WriteLine(
            "--- block length " + DecisiveBlockLength.ToString(CultureInfo.InvariantCulture)
            + ", bottom ranks, every rule that fires ---");
        TestContext.Out.WriteLine(
            "variant | rank | slotDaysSince | weeklyLoad | score | clears | max reject | fired rules");

        foreach (var row in rows)
        {
            TestContext.Out.WriteLine(
                row.Variant
                + RuleSeparator + row.Rank.ToString(CultureInfo.InvariantCulture)
                + RuleSeparator + Value(row.SlotDaysSince)
                + RuleSeparator + Value(row.WeeklyLoad)
                + RuleSeparator + Format(row.Score)
                + RuleSeparator + (row.Score >= AcceptanceThreshold ? Yes : No)
                + RuleSeparator + Activation(row.MaxRejectActivation)
                + RuleSeparator + DescribeFired(row.Fired));
        }
    }

    private static void ReportBlockLengthProfile(IReadOnlyList<RuleVariant> variants)
    {
        var rank = BottomRanks[^1];
        TestContext.Out.WriteLine(string.Empty);
        TestContext.Out.WriteLine(
            "--- reject profile over the package length, rank "
            + rank.ToString(CultureInfo.InvariantCulture)
            + ", slotDaysSince " + Value(SlotDaysSinceProbes[0])
            + ", weeklyLoad " + Value(WeeklyLoadProbes[1]) + " ---");
        TestContext.Out.WriteLine("variant | block length | score | max reject | fired rules");

        foreach (var variant in variants)
        {
            foreach (var blockLength in BlockLengthProfile)
            {
                var row = Evaluate(variant, rank, blockLength, SlotDaysSinceProbes[0], WeeklyLoadProbes[1]);
                TestContext.Out.WriteLine(
                    row.Variant
                    + RuleSeparator + blockLength.ToString(CultureInfo.InvariantCulture)
                    + RuleSeparator + Format(row.Score)
                    + RuleSeparator + Activation(row.MaxRejectActivation)
                    + RuleSeparator + DescribeFired(row.Fired));
            }
        }
    }

    private static void ReportVerdicts(IReadOnlyList<SweepRow> rows, IReadOnlyList<RuleVariant> variants)
    {
        TestContext.Out.WriteLine(string.Empty);
        TestContext.Out.WriteLine("--- verdicts ---");

        foreach (var variant in variants)
        {
            var ofVariant = rows.Where(row => row.Variant == variant.Label).ToList();
            var best = ofVariant.Max(row => row.Score);
            var worstReject = ofVariant.Max(row => row.MaxRejectActivation);
            TestContext.Out.WriteLine(
                variant.Label
                + RuleSeparator + "best score over the sweep " + Format(best)
                + RuleSeparator + "highest reject activation " + Activation(worstReject)
                + RuleSeparator + (best >= AcceptanceThreshold
                    ? "a bottom rank CAN clear the threshold on the fifth package day"
                    : "no swept situation clears the threshold on the fifth package day"));
        }

        var gatedRows = rows.Where(row => row.Variant == SatedGatedLabel).ToList();
        var r16Activation = gatedRows
            .SelectMany(row => row.Fired)
            .Where(rule => rule.RuleName == BottomIndexSatedRuleName)
            .Select(rule => rule.Activation)
            .DefaultIfEmpty(0.0)
            .Max();

        TestContext.Out.WriteLine(string.Empty);
        TestContext.Out.WriteLine(
            "finding 1b (R16 saturates the bottom ranks just like R11): "
            + (r16Activation >= 1.0 ? Confirmed : Refuted)
            + " — with R11 gated on MidRun, " + BottomIndexSatedRuleName
            + " still fires at up to " + Activation(r16Activation)
            + ". At or above 1.00 the E4c commit has to gate R16 together with R11; below that, gating R11 "
            + "alone is enough.");

        var advocateRows = rows.Where(row => row.Variant == AdvocateLabel).ToList();
        var advocateBest = advocateRows.Count == 0 ? 0.0 : advocateRows.Max(row => row.Score);
        var advocateWorst = advocateRows.Count == 0 ? 0.0 : advocateRows.Min(row => row.Score);

        TestContext.Out.WriteLine(
            "advocate check (R41_PackageHold at Hold = " + Value(PackageTypeMatchHold) + "): score range "
            + Format(advocateWorst) + " .. " + Format(advocateBest)
            + " against the threshold " + Format(AcceptanceThreshold)
            + " — " + (advocateWorst >= AcceptanceThreshold
                ? "the advocate carries every swept situation over the threshold"
                : advocateBest >= AcceptanceThreshold
                    ? "the advocate carries some but not all swept situations over the threshold"
                    : "the advocate does not carry a single swept situation over the threshold"));
    }

    private static string DescribeFired(IReadOnlyList<RuleActivation> fired)
        => fired.Count == 0
            ? None
            : string.Join(ListSeparator, fired
                .OrderByDescending(rule => rule.Activation)
                .ThenBy(rule => rule.RuleName, StringComparer.Ordinal)
                .Select(rule => rule.RuleName + "=" + rule.ConsequentTerm + "@" + Activation(rule.Activation)));

    private static string Format(double value) => value.ToString(ScoreFormat, CultureInfo.InvariantCulture);

    private static string Activation(double value) => value.ToString(ActivationFormat, CultureInfo.InvariantCulture);

    private static string Value(double value) => value.ToString(ValueFormat, CultureInfo.InvariantCulture);

    /// <param name="Label">Name of the rule-base variant in the report</param>
    /// <param name="Rules">Rule base used for this variant; built in memory, never written to disk</param>
    private sealed record RuleVariant(string Label, IReadOnlyList<FuzzyRule> Rules);

    /// <param name="Variant">Rule-base variant this row was produced with</param>
    /// <param name="Rank">One-based roster rank of the bidding employee</param>
    /// <param name="BlockLength">Days the package already ran when the bid is placed</param>
    /// <param name="SlotDaysSince">Days since the employee last worked the slot's kind</param>
    /// <param name="WeeklyLoad">Accumulated hours divided by the weekly cap</param>
    /// <param name="Score">Defuzzified bid score</param>
    /// <param name="MaxRejectActivation">Strongest activation of any rule pointing at the Reject term</param>
    /// <param name="Fired">Every rule that fired, with its activation</param>
    private sealed record SweepRow(
        string Variant,
        int Rank,
        int BlockLength,
        double SlotDaysSince,
        double WeeklyLoad,
        double Score,
        double MaxRejectActivation,
        IReadOnlyList<RuleActivation> Fired);
}
