// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.Services.Schedules.HolisticHarmonizer.Critic;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Services.Schedules.HolisticHarmonizer.Critic;

/// <summary>
/// A vision model reads grids imprecisely; only findings whose every cell matches the real plan may reach the user.
/// </summary>
[TestFixture]
public sealed class PlanCriticFindingVerifierTests
{
    private const int MaxFindings = 10;

    private static PlanCriticFinding Finding(string pattern, params PlanCriticCell[] cells)
        => new(pattern, "description", PlanCriticSeverity.Medium, cells);

    [Test]
    public void FindingWhoseCellsMatch_IsKept()
    {
        var finding = Finding("night_then_early", new PlanCriticCell(0, 0, "N"), new PlanCriticCell(0, 1, "E"));

        var (verified, discarded) = PlanCriticFindingVerifier.Verify(PlanCriticTestPlans.TwoAgentsThreeDays(), [finding], MaxFindings);

        verified.Count.ShouldBe(1);
        discarded.ShouldBe(0);
    }

    [Test]
    public void FreeCell_IsCitedWithUnderscore()
    {
        var finding = Finding("isolated_free_day", new PlanCriticCell(1, 0, "_"));

        var (verified, _) = PlanCriticFindingVerifier.Verify(PlanCriticTestPlans.TwoAgentsThreeDays(), [finding], MaxFindings);

        verified.Count.ShouldBe(1);
    }

    [Test]
    public void OneWrongSymbol_DiscardsTheWholeFinding()
    {
        var finding = Finding("invented", new PlanCriticCell(0, 0, "N"), new PlanCriticCell(1, 0, "N"));

        var (verified, discarded) = PlanCriticFindingVerifier.Verify(PlanCriticTestPlans.TwoAgentsThreeDays(), [finding], MaxFindings);

        verified.ShouldBeEmpty();
        discarded.ShouldBe(1);
    }

    [TestCase(-1, 0)]
    [TestCase(2, 0)]
    [TestCase(0, 3)]
    [TestCase(0, -1)]
    public void CellOutsideThePlan_DiscardsTheFinding(int row, int day)
    {
        var finding = Finding("out_of_range", new PlanCriticCell(row, day, "_"));

        var (verified, discarded) = PlanCriticFindingVerifier.Verify(PlanCriticTestPlans.TwoAgentsThreeDays(), [finding], MaxFindings);

        verified.ShouldBeEmpty();
        discarded.ShouldBe(1);
    }

    [Test]
    public void FindingWithoutCellsOrPattern_IsDiscarded()
    {
        var findings = new[] { Finding("no_cells"), Finding(" ", new PlanCriticCell(0, 0, "N")) };

        var (verified, discarded) = PlanCriticFindingVerifier.Verify(PlanCriticTestPlans.TwoAgentsThreeDays(), findings, MaxFindings);

        verified.ShouldBeEmpty();
        discarded.ShouldBe(2);
    }

    [Test]
    public void SymbolsAreNormalisedAndDuplicateCellsCollapsed()
    {
        var finding = Finding(" night_then_early ", new PlanCriticCell(0, 0, " n "), new PlanCriticCell(0, 0, "N"));

        var (verified, _) = PlanCriticFindingVerifier.Verify(PlanCriticTestPlans.TwoAgentsThreeDays(), [finding], MaxFindings);

        verified.Single().Pattern.ShouldBe("night_then_early");
        verified.Single().Cells.ShouldHaveSingleItem().Symbol.ShouldBe("N");
    }

    [Test]
    public void FindingsBeyondTheCap_AreCountedAsDiscarded()
    {
        var findings = Enumerable.Range(0, 3).Select(i => Finding("p" + i, new PlanCriticCell(0, 0, "N"))).ToList();

        var (verified, discarded) = PlanCriticFindingVerifier.Verify(PlanCriticTestPlans.TwoAgentsThreeDays(), findings, 2);

        verified.Count.ShouldBe(2);
        discarded.ShouldBe(1);
    }
}
