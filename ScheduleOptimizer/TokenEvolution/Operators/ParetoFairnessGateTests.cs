// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.ScheduleOptimizer.TokenEvolution.Operators;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.TokenEvolution.Operators;

[TestFixture]
public class ParetoFairnessGateTests
{
    private const int CleanLegality = 0;
    private const int CleanStage0 = 0;
    private const double Stage1Score = 0.9;
    private const double Stage2Score = 0.8;
    private const double BlockOrderScore = 0.7;
    private const double BlacklistScore = 1.0;
    private const double KindFairnessScore = 0.6;
    private const int NoOverlongPackages = 0;
    private const int MixedPackages = 2;
    private const double Epsilon = 1e-9;

    private const string LegalityCase = "Legality";
    private const string Stage0Case = "Stage0";
    private const string Stage1Case = "Stage1";
    private const string Stage2Case = "Stage2";
    private const string BlockOrderCase = "BlockOrder";
    private const string BlacklistCase = "Blacklist";
    private const string OverlongCase = "OverlongPackages";
    private const string MixedCase = "MixedPackages";

    private static ParetoGateSnapshot Current() => new(
        Legality: CleanLegality,
        Stage0: CleanStage0,
        Stage1: Stage1Score,
        Stage2: Stage2Score,
        BlockOrder: BlockOrderScore,
        Blacklist: BlacklistScore,
        ShiftKindFairness: KindFairnessScore,
        OverlongPackages: NoOverlongPackages,
        MixedPackages: MixedPackages);

    private static ParetoGateSnapshot FairerThanCurrent() => Current() with
    {
        ShiftKindFairness = KindFairnessScore + Epsilon,
    };

    [Test]
    public void Accepts_OnlyTheFairnessRises_TakesTheSwap()
    {
        ParetoFairnessGate.Accepts(Current(), FairerThanCurrent()).ShouldBeTrue();
    }

    /// <summary>
    /// The gate is a Pareto gate, not a trade: a fairness gain never pays for a rule between 1 and 8.
    /// Each case damages exactly one protected component by the smallest representable step while the
    /// fairness rises, so a missing clause in the predicate shows up as a single failing case.
    /// </summary>
    /// <param name="damaged">Name of the protected component the candidate moves the wrong way</param>
    [TestCase(LegalityCase)]
    [TestCase(Stage0Case)]
    [TestCase(Stage1Case)]
    [TestCase(Stage2Case)]
    [TestCase(BlockOrderCase)]
    [TestCase(BlacklistCase)]
    [TestCase(OverlongCase)]
    [TestCase(MixedCase)]
    public void Accepts_AnyProtectedComponentGetsWorse_RefusesTheSwap(string damaged)
    {
        var fairer = FairerThanCurrent();
        var candidate = damaged switch
        {
            LegalityCase => fairer with { Legality = CleanLegality + 1 },
            Stage0Case => fairer with { Stage0 = CleanStage0 + 1 },
            Stage1Case => fairer with { Stage1 = Stage1Score - Epsilon },
            Stage2Case => fairer with { Stage2 = Stage2Score - Epsilon },
            BlockOrderCase => fairer with { BlockOrder = BlockOrderScore - Epsilon },
            BlacklistCase => fairer with { Blacklist = BlacklistScore - Epsilon },
            OverlongCase => fairer with { OverlongPackages = NoOverlongPackages + 1 },
            MixedCase => fairer with { MixedPackages = MixedPackages + 1 },
            _ => throw new ArgumentOutOfRangeException(nameof(damaged), damaged, null),
        };

        ParetoFairnessGate.Accepts(Current(), candidate).ShouldBeFalse();
    }

    /// <summary>
    /// Every protected component may improve at the same time — the gate demands "not worse", not
    /// "unchanged".
    /// </summary>
    [Test]
    public void Accepts_EveryProtectedComponentImprovesToo_TakesTheSwap()
    {
        var candidate = new ParetoGateSnapshot(
            Legality: CleanLegality,
            Stage0: CleanStage0,
            Stage1: Stage1Score + Epsilon,
            Stage2: Stage2Score + Epsilon,
            BlockOrder: BlockOrderScore + Epsilon,
            Blacklist: BlacklistScore + Epsilon,
            ShiftKindFairness: KindFairnessScore + Epsilon,
            OverlongPackages: NoOverlongPackages,
            MixedPackages: MixedPackages - 1);

        ParetoFairnessGate.Accepts(Current(), candidate).ShouldBeTrue();
    }

    /// <summary>
    /// Rule 9 must be strictly better. An equal or falling fairness makes the swap pointless, and with
    /// a falling one the gate would hand out free order-loyalty losses.
    /// </summary>
    /// <param name="candidateFairness">Shift-kind fairness of the proposed plan</param>
    [TestCase(KindFairnessScore)]
    [TestCase(KindFairnessScore - Epsilon)]
    public void Accepts_FairnessDoesNotRiseStrictly_RefusesTheSwap(double candidateFairness)
    {
        var candidate = Current() with { ShiftKindFairness = candidateFairness };

        ParetoFairnessGate.Accepts(Current(), candidate).ShouldBeFalse();
    }
}
