// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Globalization;
using Klacks.UnitTest.Autofill.Fixtures;
using Klacks.UnitTest.Autofill.Scenarios.Scenario2;

namespace Klacks.UnitTest.Autofill.Scenarios.Scenario3;

/// <summary>
/// Builds the engine-level runs of specification scenario 3: the unchanged scenario 2 fixture plus a
/// hand-enumerated ban list per run. L0 carries no ban list (control group), L1 the full 73 triples
/// of the keyword table, L4 bans all five employees from every night (155 triples, the A22
/// unstaffability case, modelled as `Missing` per owner decision S3-1), and L5 bans only MA-3 and
/// MA-4 (62 triples) to isolate the pool effect from the expiry effect. The engine runs presuppose
/// the expiry semantics — the 11 MA-2 triples of L1 are the RESULT of "valid until 2026-03-20", not
/// a test of it; that proof lives on API level (EligibilityMatrixBuilder, A20).
/// <para>
/// The validation entry points exist so a broken setup is reported as a setup error and never as a
/// red rule assertion. On top of the scenario 2 consistency check they verify the hand arithmetic of
/// the triple counts and — for L1 and L5 — the specification sentence "the carry-in stays keyword
/// conform": no February carry-in day is banned, no expected in-period continuation of an open
/// package is banned, and the rotation targets of the closed packages (MA-4 late from 02.03., MA-5
/// night from 03.03.) are open to their employees. L4 checks only the February stock: its March
/// nights are unstaffable BY DESIGN, so demanding conform continuations there would reject the very
/// fixture the assertion needs.
/// </para>
/// </summary>
public static class Scenario3EligibilityFixture
{
    private static readonly IReadOnlyDictionary<Guid, AutofillShiftKind> ShiftKinds =
        new Dictionary<Guid, AutofillShiftKind>
        {
            [AutofillShiftCatalog.EarlyShiftId] = AutofillShiftKind.Early,
            [AutofillShiftCatalog.LateShiftId] = AutofillShiftKind.Late,
            [AutofillShiftCatalog.NightShiftId] = AutofillShiftKind.Night,
        };

    private static readonly IReadOnlyDictionary<string, DateOnly> ClosedPackageExpectedStarts =
        new Dictionary<string, DateOnly>(StringComparer.Ordinal)
        {
            [Scenario2SpecValues.Ma4] = Scenario2SpecValues.Ma4ExpectedNewPackageStart,
            [Scenario2SpecValues.Ma5] = Scenario2SpecValues.Ma5ExpectedNewPackageStart,
        };

    /// <summary>L0: the unchanged scenario 2 fixture — the control group every diff is taken against.</summary>
    public static AutofillScenarioDefinition BuildL0() => Scenario2CarryInFixture.Build();

    /// <summary>L1: the main run with the full keyword table resolved to 73 ban triples.</summary>
    public static AutofillScenarioDefinition BuildL1() => Scenario2CarryInFixture.Build(L1Eligibility());

    /// <summary>
    /// L1 restrictions on top of a frozen plan head — the frozen-prefix replanning case: the given
    /// locked works are the assignments of an existing plan before the replan date.
    /// </summary>
    /// <param name="lockedWorks">Frozen assignments the run must keep</param>
    public static AutofillScenarioDefinition BuildL1(
        IReadOnlyList<Klacks.ScheduleOptimizer.Models.CoreLockedWork> lockedWorks)
        => Scenario2CarryInFixture.Build(L1Eligibility(), lockedWorks);

    /// <summary>L4: NACHT-BEF removed from everybody — every night slot has an empty pool.</summary>
    public static AutofillScenarioDefinition BuildL4() => Scenario2CarryInFixture.Build(L4Eligibility());

    /// <summary>L5: MA-2's NACHT-BEF unbounded — only the two never-eligible employees are banned.</summary>
    public static AutofillScenarioDefinition BuildL5() => Scenario2CarryInFixture.Build(L5Eligibility());

    /// <summary>
    /// The 73 triples of the main run: MA-3 and MA-4 lack NACHT-BEF for the whole period, MA-2's
    /// assignment is valid until 2026-03-20 and therefore banned from 2026-03-21 on.
    /// </summary>
    public static AutofillEligibilityInput L1Eligibility()
    {
        var bans = new HashSet<(string AgentId, Guid ShiftId, DateOnly Date)>();
        AddNightBan(bans, Scenario2SpecValues.Ma3, AutofillSpecConstants.PeriodFrom);
        AddNightBan(bans, Scenario2SpecValues.Ma4, AutofillSpecConstants.PeriodFrom);
        AddNightBan(bans, Scenario2SpecValues.Ma2, Scenario3SpecValues.Ma2NightBanFrom);

        return new AutofillEligibilityInput(bans, ShiftKinds)
        {
            KeywordFacts = new Dictionary<(string AgentId, Guid ShiftId), AutofillKeywordFact>
            {
                [(Scenario2SpecValues.Ma3, AutofillShiftCatalog.NightShiftId)] = MissingNightKeyword(),
                [(Scenario2SpecValues.Ma4, AutofillShiftCatalog.NightShiftId)] = MissingNightKeyword(),
                [(Scenario2SpecValues.Ma2, AutofillShiftCatalog.NightShiftId)] = new AutofillKeywordFact(
                    Scenario3SpecValues.NightKeyword, null, Scenario3SpecValues.Ma2NightValidUntil),
            },
        };
    }

    /// <summary>
    /// The 155 triples of the unstaffability run: NACHT-BEF removed from all five employees (the
    /// `Missing` branch, per owner decision S3-1), so no night slot of the period has any eligible
    /// employee left.
    /// </summary>
    public static AutofillEligibilityInput L4Eligibility()
    {
        var bans = new HashSet<(string AgentId, Guid ShiftId, DateOnly Date)>();
        var facts = new Dictionary<(string AgentId, Guid ShiftId), AutofillKeywordFact>();
        for (var rank = 1; rank <= AutofillSpecConstants.EmployeeCount; rank++)
        {
            var agentId = AutofillSpecConstants.EmployeeIdPrefix + rank.ToString(CultureInfo.InvariantCulture);
            AddNightBan(bans, agentId, AutofillSpecConstants.PeriodFrom);
            facts[(agentId, AutofillShiftCatalog.NightShiftId)] = MissingNightKeyword();
        }

        return new AutofillEligibilityInput(bans, ShiftKinds) { KeywordFacts = facts };
    }

    /// <summary>
    /// The 62 triples of the diagnostic run: only the two never-eligible employees are banned; MA-2
    /// keeps every night day. Differences between L5 and L1 may therefore come only from MA-2's
    /// expiry, i.e. from 2026-03-21 on.
    /// </summary>
    public static AutofillEligibilityInput L5Eligibility()
    {
        var bans = new HashSet<(string AgentId, Guid ShiftId, DateOnly Date)>();
        AddNightBan(bans, Scenario2SpecValues.Ma3, AutofillSpecConstants.PeriodFrom);
        AddNightBan(bans, Scenario2SpecValues.Ma4, AutofillSpecConstants.PeriodFrom);

        return new AutofillEligibilityInput(bans, ShiftKinds)
        {
            KeywordFacts = new Dictionary<(string AgentId, Guid ShiftId), AutofillKeywordFact>
            {
                [(Scenario2SpecValues.Ma3, AutofillShiftCatalog.NightShiftId)] = MissingNightKeyword(),
                [(Scenario2SpecValues.Ma4, AutofillShiftCatalog.NightShiftId)] = MissingNightKeyword(),
            },
        };
    }

    /// <summary>
    /// Scenario 2 consistency plus the scenario 3 additions: an eligibility input must be present
    /// and its triple count must equal the hand arithmetic of the specification — a silent
    /// enumeration mistake here would quietly turn every pool measurement into fiction.
    /// </summary>
    /// <param name="definition">Assembled scenario to check</param>
    /// <param name="expectedTripleCount">Ban triples the specification derives for this run</param>
    public static IReadOnlyList<string> Validate(AutofillScenarioDefinition definition, int expectedTripleCount)
    {
        var problems = new List<string>(Scenario2CarryInFixture.Validate(definition));

        if (definition.Eligibility is null)
        {
            problems.Add("The scenario carries no eligibility input, so it is scenario 2, not a scenario 3 run.");
            return problems;
        }

        var actual = definition.Eligibility.IneligibleAssignments.Count;
        if (actual != expectedTripleCount)
        {
            problems.Add(
                $"The ban list holds {actual.ToString(CultureInfo.InvariantCulture)} triples, but the "
                + $"specification arithmetic demands exactly "
                + $"{expectedTripleCount.ToString(CultureInfo.InvariantCulture)}.");
        }

        return problems;
    }

    /// <summary>
    /// The February half of "the carry-in stays keyword conform": no fixed previous-month work day
    /// may be banned. Holds for every run of the family, L4 included, because all ban triples lie in
    /// March.
    /// </summary>
    /// <param name="definition">Assembled scenario to check</param>
    public static IReadOnlyList<string> CarryInStockConformityProblems(AutofillScenarioDefinition definition)
    {
        var problems = new List<string>();
        var eligibility = definition.Eligibility;
        if (eligibility is null)
        {
            return problems;
        }

        foreach (var carryIn in definition.CarryIns)
        {
            var shiftId = AutofillShiftCatalog.ShiftIdOf(carryIn.Kind);
            foreach (var date in carryIn.Days())
            {
                if (eligibility.IneligibleAssignments.Contains((carryIn.AgentId, shiftId, date)))
                {
                    problems.Add(
                        $"{carryIn.AgentId} holds a fixed {carryIn.Kind} shift on {date:yyyy-MM-dd}, but the ban "
                        + "list forbids exactly that triple — the carried-in stock itself would violate the "
                        + "keyword table, so no run result could be attributed to the algorithm.");
                }
            }
        }

        return problems;
    }

    /// <summary>
    /// The March half of the conformity sentence, for L1 and L5: every open package must be
    /// completable (its kind unbanned for the employee over the expected remaining days) and every
    /// closed package's rotation target must be open to its employee over a full package from the
    /// expected start — MA-1 night 01.–03.03., MA-2 late 01.03., MA-3 early 01.–04.03., MA-4 late
    /// from 02.03., MA-5 night from 03.03. A ban on any of these days would force the very violation
    /// A10 to A13 are supposed to measure freely.
    /// </summary>
    /// <param name="definition">Assembled scenario to check</param>
    public static IReadOnlyList<string> CarryInContinuationConformityProblems(AutofillScenarioDefinition definition)
    {
        var problems = new List<string>();
        var eligibility = definition.Eligibility;
        if (eligibility is null)
        {
            return problems;
        }

        foreach (var carryIn in definition.OpenCarryIns)
        {
            var lastExpectedDay = definition.PeriodFrom.AddDays(carryIn.ExpectedRemainingDays - 1);
            AddWindowProblems(
                problems, eligibility, carryIn.AgentId, carryIn.Kind, definition.PeriodFrom, lastExpectedDay,
                "the expected completion of its open previous-month package");
        }

        foreach (var carryIn in definition.CarryIns.Where(c => !c.IsOpenAt(definition.PeriodFrom)))
        {
            if (!ClosedPackageExpectedStarts.TryGetValue(carryIn.AgentId, out var expectedStart))
            {
                problems.Add(
                    $"{carryIn.AgentId} closed its previous-month package, but the specification table names no "
                    + "expected new package start for it — the conformity check cannot cover it.");
                continue;
            }

            var lastExpectedDay = expectedStart.AddDays(AutofillSpecConstants.MaxWorkDays - 1);
            AddWindowProblems(
                problems, eligibility, carryIn.AgentId, carryIn.ExpectedFirstShiftKind, expectedStart,
                lastExpectedDay, "the expected rotated package after its closed previous-month package");
        }

        return problems;
    }

    private static void AddWindowProblems(
        List<string> problems,
        AutofillEligibilityInput eligibility,
        string agentId,
        AutofillShiftKind kind,
        DateOnly from,
        DateOnly until,
        string windowDescription)
    {
        var shiftId = AutofillShiftCatalog.ShiftIdOf(kind);
        for (var date = from; date <= until; date = date.AddDays(1))
        {
            if (eligibility.IneligibleAssignments.Contains((agentId, shiftId, date)))
            {
                problems.Add(
                    $"{agentId} is banned from {kind} on {date:yyyy-MM-dd}, which lies inside "
                    + $"{windowDescription} ({from:yyyy-MM-dd}..{until:yyyy-MM-dd}) — the fixture would force a "
                    + "carry-in violation instead of leaving it to the algorithm.");
            }
        }
    }

    private static void AddNightBan(
        HashSet<(string AgentId, Guid ShiftId, DateOnly Date)> bans, string agentId, DateOnly from)
    {
        foreach (var triple in AutofillEligibilityInput.BanTriples(
                     agentId, AutofillShiftCatalog.NightShiftId, from, AutofillSpecConstants.PeriodUntil))
        {
            bans.Add(triple);
        }
    }

    private static AutofillKeywordFact MissingNightKeyword()
        => new(Scenario3SpecValues.NightKeyword, null, null);
}
