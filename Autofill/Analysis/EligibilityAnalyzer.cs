// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Globalization;
using Klacks.ScheduleOptimizer.Models;
using Klacks.UnitTest.Autofill.Analysis.Model;
using Klacks.UnitTest.Autofill.Fixtures;

namespace Klacks.UnitTest.Autofill.Analysis;

/// <summary>
/// Everything the analyzer derives from the fixture ban list — pools, keyword violations, forced
/// rotation deviations, forced package shortenings and the night cohort — separated out so the main
/// analyzer stays the plan reader and this class stays the restriction reader.
/// <para>
/// Three measurement decisions apply throughout. First, ACTIVE means a non-empty ban list: the
/// engine short-circuits an empty set to "everyone is eligible" (finding K3), so an empty list
/// carries no eligibility knowledge and every method here returns its empty result — scenarios
/// without keyword restrictions measure exactly as they did before this class existed. Second, all
/// keyword-violation evidence comes from scanning the finished plan against the ban list, never from
/// the engine's ConstraintViolation list, because the engine's counter skips locked assignments and
/// would exempt exactly the carried-in stock (finding K8); the scan therefore covers the carry-in
/// days as well. Third, the ban list is the ONLY provable cause available on engine level: a reason
/// or a forced flag is claimed exactly where the list closes every alternative, and everything else
/// stays unexplained rather than guessed (finding K8b).
/// </para>
/// <para>
/// The forced-shortening bound follows the specification's own capacity arithmetic. For each shift
/// kind the ban list mentions, the period is cut into maximal windows with a constant eligible pool.
/// Within a window of w days and n pool employees, at most floor((w + MinRestDays) /
/// (MaxWorkDays + MinRestDays)) full five-day packages fit per employee under the 5-work/2-free
/// model, so at most n times that many times five shifts can lie in full packages; every demanded
/// shift beyond that capacity forces a shortened package, and one shortened package absorbs at most
/// four shifts. The bound treats packages as confined to their window — a package spanning a
/// pool-change day can in rare geometries cover a window day from a full package outside it, which
/// makes the bound conservative in the attribution direction: it can leave a genuinely forced
/// shortening unattributed (reported as unexplained, a red for phase D to judge), but it never
/// stamps "forced" on more packages than the arithmetic proves. Windows with an empty pool are
/// skipped: their days produce unfillable slots, not shortenings.
/// </para>
/// </summary>
public static class EligibilityAnalyzer
{
    private const int SingletonPoolSize = 1;
    private const int MinCohortEligibleDays = 1;
    private const string CauseDateFormat = "yyyy-MM-dd";

    /// <summary>Shifts a shortened package can still hold — one less than a full package.</summary>
    private const int MaxShiftsInShortenedPackage = AutofillSpecConstants.MaxWorkDays - 1;

    /// <summary>True when the scenario carries eligibility knowledge the engine actually enforces.</summary>
    /// <param name="definition">Scenario the plan was produced under</param>
    public static bool IsActive(AutofillScenarioDefinition definition)
        => definition.Eligibility is { } eligibility && eligibility.IneligibleAssignments.Count > 0;

    /// <summary>
    /// Fails loudly when the eligibility input does not fit the scenario it is attached to. A silent
    /// mismatch would measure empty pools where the fixture meant restrictions.
    /// </summary>
    /// <param name="definition">Scenario the plan was produced under</param>
    public static void EnsureUsable(AutofillScenarioDefinition definition)
    {
        if (!IsActive(definition))
        {
            return;
        }

        var problems = definition.Eligibility!.ValidationProblems(
            definition.Context, definition.EmployeesInListOrder);
        if (problems.Count > 0)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, problems));
        }
    }

    /// <summary>
    /// The input-side pools: who may hold each demanded slot, plus the slots that are factually
    /// determined (pool of one) or unfillable (pool of zero) before the engine runs.
    /// </summary>
    /// <param name="definition">Scenario the plan was produced under</param>
    public static EligibilityMetrics BuildEligibility(AutofillScenarioDefinition definition)
    {
        if (!IsActive(definition))
        {
            return new EligibilityMetrics([], [], []);
        }

        var eligibility = definition.Eligibility!;
        var pools = new List<ShiftEligibilityPool>();
        var singletons = new List<SingletonPoolDay>();
        var empties = new List<EmptyPoolDay>();

        foreach (var slot in SlotsInOrder(definition))
        {
            var eligibleEmployees = definition.EmployeesInListOrder
                .Where(e => !eligibility.IneligibleAssignments.Contains((e, slot.ShiftId, slot.Date)))
                .ToList();

            pools.Add(new ShiftEligibilityPool(slot.Date, slot.Kind, eligibleEmployees, eligibleEmployees.Count));
            if (eligibleEmployees.Count == SingletonPoolSize)
            {
                singletons.Add(new SingletonPoolDay(slot.Date, slot.Kind, eligibleEmployees[0]));
            }

            if (eligibleEmployees.Count == 0)
            {
                empties.Add(new EmptyPoolDay(slot.Date, slot.Kind));
            }
        }

        return new EligibilityMetrics(pools, singletons, empties);
    }

    /// <summary>
    /// The independent ban-list scan of the finished plan: every planned token AND every carried-in
    /// shift whose (agent, shift, date) triple the ban list forbids. Keyword name and window come
    /// from the fixture facts, with the uniform placeholder where the fixture named none.
    /// </summary>
    /// <param name="scenario">Plan produced by the engine</param>
    /// <param name="definition">Scenario that produced it; supplies ban list and carry-in days</param>
    public static KeywordMetrics BuildKeyword(CoreScenario scenario, AutofillScenarioDefinition definition)
    {
        if (!IsActive(definition))
        {
            return new KeywordMetrics([]);
        }

        var eligibility = definition.Eligibility!;
        var violations = new List<KeywordViolation>();

        foreach (var token in scenario.Tokens)
        {
            if (!eligibility.IneligibleAssignments.Contains((token.AgentId, token.ShiftRefId, token.Date)))
            {
                continue;
            }

            var kind = eligibility.ShiftKindsById.TryGetValue(token.ShiftRefId, out var mapped)
                ? mapped
                : AutofillShiftCatalog.FromShiftTypeIndex(token.ShiftTypeIndex);
            violations.Add(BuildViolation(eligibility, token.AgentId, token.ShiftRefId, token.Date, kind));
        }

        foreach (var carryIn in definition.CarryIns)
        {
            var shiftId = AutofillShiftCatalog.ShiftIdOf(carryIn.Kind);
            foreach (var date in carryIn.Days())
            {
                if (eligibility.IneligibleAssignments.Contains((carryIn.AgentId, shiftId, date)))
                {
                    violations.Add(BuildViolation(eligibility, carryIn.AgentId, shiftId, date, carryIn.Kind));
                }
            }
        }

        return new KeywordMetrics(violations
            .OrderBy(v => definition.ListRankOf(v.Employee))
            .ThenBy(v => v.Date)
            .ThenBy(v => v.ShiftType)
            .ToList());
    }

    /// <summary>
    /// The reason word of one package-to-package transition. A forward transition needs no reason; a
    /// deviation is keywordIneligible when the ban list closes the forward successor kind for the
    /// employee on EVERY in-period day of the following package — only then was the forward step
    /// provably impossible — and unexplained otherwise, including everything a scenario without a ban
    /// list cannot prove.
    /// </summary>
    /// <param name="employee">Employee making the transition</param>
    /// <param name="fromKind">Kind of the earlier package</param>
    /// <param name="forward">Whether the transition already follows the rotation direction</param>
    /// <param name="nextPackage">The package the employee actually went to</param>
    /// <param name="definition">Scenario the plan was produced under</param>
    public static string TransitionReasonOf(
        string employee,
        AutofillShiftKind fromKind,
        bool forward,
        WorkPackage nextPackage,
        AutofillScenarioDefinition definition)
    {
        if (forward)
        {
            return RotationTransitionReason.None;
        }

        if (!IsActive(definition))
        {
            return RotationTransitionReason.Unexplained;
        }

        return ForwardStepProvablyBanned(employee, ForwardSuccessorOf(fromKind), nextPackage, definition)
            ? RotationTransitionReason.KeywordIneligible
            : RotationTransitionReason.Unexplained;
    }

    /// <summary>
    /// Splits the shortened packages of every ban-restricted kind into the provably forced ones and
    /// the rest. Kinds the ban list never mentions are outside this measure — their shortness is
    /// what the length histogram already reports.
    /// </summary>
    /// <param name="allPackages">Every listed package of the plan</param>
    /// <param name="definition">Scenario the plan was produced under</param>
    public static (IReadOnlyList<ForcedShortening> Forced, int Unexplained) BuildShortenings(
        IReadOnlyList<WorkPackage> allPackages, AutofillScenarioDefinition definition)
    {
        if (!IsActive(definition))
        {
            return ([], 0);
        }

        var eligibility = definition.Eligibility!;
        var restrictedKinds = eligibility.IneligibleAssignments
            .Select(b => eligibility.ShiftKindsById[b.ShiftId])
            .Distinct()
            .OrderBy(k => k)
            .ToList();

        var forced = new List<ForcedShortening>();
        var shortenedTotal = 0;

        foreach (var kind in restrictedKinds)
        {
            var shortened = allPackages
                .Where(p => p.ShiftType == kind && p.LengthDays < AutofillSpecConstants.MaxWorkDays)
                .OrderBy(p => p.StartDate)
                .ThenBy(p => definition.ListRankOf(p.Employee))
                .ToList();
            shortenedTotal += shortened.Count;

            var attributed = new HashSet<WorkPackage>();
            foreach (var window in ForcedWindowsOf(kind, definition))
            {
                var budget = window.Budget;
                foreach (var candidate in shortened)
                {
                    if (budget == 0)
                    {
                        break;
                    }

                    if (attributed.Contains(candidate)
                        || candidate.EndDate < window.From
                        || candidate.StartDate > window.Until)
                    {
                        continue;
                    }

                    attributed.Add(candidate);
                    forced.Add(new ForcedShortening(
                        candidate.Employee, candidate.StartDate, candidate.LengthDays, window.Cause));
                    budget--;
                }
            }
        }

        return (forced, shortenedTotal - forced.Count);
    }

    /// <summary>
    /// The night cohort: per-employee night load divided by the days the ban list leaves a night slot
    /// open to that employee, and the spread of that share inside the cohort of employees with at
    /// least one eligible night day.
    /// </summary>
    /// <param name="counts">Per-employee shift-kind counts of the plan, in list order</param>
    /// <param name="definition">Scenario the plan was produced under</param>
    public static (IReadOnlyList<NightEligibilityShare> Shares, double CohortSpread) BuildNightShares(
        IReadOnlyList<EmployeeShiftTypeCounts> counts, AutofillScenarioDefinition definition)
    {
        if (!IsActive(definition))
        {
            return ([], 0);
        }

        var eligibility = definition.Eligibility!;
        var nightSlots = SlotsInOrder(definition)
            .Where(s => s.Kind == AutofillShiftKind.Night)
            .ToList();

        var shares = new List<NightEligibilityShare>();
        foreach (var count in counts)
        {
            var eligibleDays = nightSlots
                .Where(s => !eligibility.IneligibleAssignments.Contains((count.Employee, s.ShiftId, s.Date)))
                .Select(s => s.Date)
                .Distinct()
                .Count();

            shares.Add(new NightEligibilityShare(
                Employee: count.Employee,
                EligibleDays: eligibleDays,
                NightShifts: count.Night,
                SharePerEligibleDay: eligibleDays == 0 ? 0 : count.Night / (double)eligibleDays));
        }

        var cohort = shares.Where(s => s.EligibleDays >= MinCohortEligibleDays).ToList();
        var spread = cohort.Count <= 1
            ? 0
            : cohort.Max(s => s.SharePerEligibleDay) - cohort.Min(s => s.SharePerEligibleDay);

        return (shares, spread);
    }

    private static KeywordViolation BuildViolation(
        AutofillEligibilityInput eligibility, string agentId, Guid shiftId, DateOnly date, AutofillShiftKind kind)
    {
        var fact = eligibility.FactOf(agentId, shiftId);
        return new KeywordViolation(agentId, date, kind, fact.MissingKeyword, fact.ValidFrom, fact.ValidUntil);
    }

    private static AutofillShiftKind ForwardSuccessorOf(AutofillShiftKind kind)
        => (AutofillShiftKind)(((int)kind + 1) % AutofillShiftCatalog.Kinds.Count);

    private static bool ForwardStepProvablyBanned(
        string employee, AutofillShiftKind successor, WorkPackage nextPackage, AutofillScenarioDefinition definition)
    {
        var eligibility = definition.Eligibility!;
        var successorIds = ShiftIdsOfKind(eligibility, successor);
        if (successorIds.Count == 0)
        {
            return false;
        }

        var from = nextPackage.StartDate < definition.PeriodFrom ? definition.PeriodFrom : nextPackage.StartDate;
        if (nextPackage.EndDate < from)
        {
            return false;
        }

        for (var date = from; date <= nextPackage.EndDate; date = date.AddDays(1))
        {
            foreach (var shiftId in successorIds)
            {
                if (!eligibility.IneligibleAssignments.Contains((employee, shiftId, date)))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static List<Guid> ShiftIdsOfKind(AutofillEligibilityInput eligibility, AutofillShiftKind kind)
        => eligibility.ShiftKindsById
            .Where(pair => pair.Value == kind)
            .Select(pair => pair.Key)
            .OrderBy(id => id)
            .ToList();

    private static List<(DateOnly Date, Guid ShiftId, AutofillShiftKind Kind, int Required)> SlotsInOrder(
        AutofillScenarioDefinition definition)
    {
        var eligibility = definition.Eligibility!;
        return definition.Context.Shifts
            .Select(s =>
            {
                var shiftId = Guid.Parse(s.Id);
                return (
                    Date: DateOnly.ParseExact(s.Date, AutofillSpecConstants.IsoDateFormat),
                    ShiftId: shiftId,
                    Kind: eligibility.ShiftKindsById[shiftId],
                    Required: s.RequiredAssignments);
            })
            .OrderBy(s => s.Date)
            .ThenBy(s => s.Kind)
            .ThenBy(s => s.ShiftId)
            .ToList();
    }

    private static List<(DateOnly From, DateOnly Until, int Budget, string Cause)> ForcedWindowsOf(
        AutofillShiftKind kind, AutofillScenarioDefinition definition)
    {
        var eligibility = definition.Eligibility!;
        var kindIds = ShiftIdsOfKind(eligibility, kind);
        var slotsOfKind = SlotsInOrder(definition).Where(s => s.Kind == kind).ToList();

        var poolPerDay = new List<(DateOnly Date, IReadOnlyList<string> Pool)>();
        for (var date = definition.PeriodFrom; date <= definition.PeriodUntil; date = date.AddDays(1))
        {
            var day = date;
            var pool = definition.EmployeesInListOrder
                .Where(e => kindIds.Any(id => !eligibility.IneligibleAssignments.Contains((e, id, day))))
                .ToList();
            poolPerDay.Add((date, pool));
        }

        var windows = new List<(DateOnly From, DateOnly Until, int Budget, string Cause)>();
        var index = 0;
        while (index < poolPerDay.Count)
        {
            var start = index;
            while (index + 1 < poolPerDay.Count
                   && poolPerDay[index + 1].Pool.SequenceEqual(poolPerDay[start].Pool, StringComparer.Ordinal))
            {
                index++;
            }

            var from = poolPerDay[start].Date;
            var until = poolPerDay[index].Date;
            var poolSize = poolPerDay[start].Pool.Count;
            index++;

            if (poolSize == 0)
            {
                continue;
            }

            var windowDays = until.DayNumber - from.DayNumber + 1;
            var demand = slotsOfKind
                .Where(s => s.Date >= from && s.Date <= until)
                .Sum(s => s.Required);
            var fullPackagesPerEmployee =
                (windowDays + AutofillSpecConstants.MinRestDays)
                / (AutofillSpecConstants.MaxWorkDays + AutofillSpecConstants.MinRestDays);
            var fullPackageCapacity = poolSize * fullPackagesPerEmployee * AutofillSpecConstants.MaxWorkDays;
            if (demand <= fullPackageCapacity)
            {
                continue;
            }

            var budget = (int)Math.Ceiling(
                (demand - fullPackageCapacity) / (double)MaxShiftsInShortenedPackage);
            var cause = string.Create(
                CultureInfo.InvariantCulture,
                $"{demand} {kind} shift(s) on {from.ToString(CauseDateFormat, CultureInfo.InvariantCulture)}"
                + $"..{until.ToString(CauseDateFormat, CultureInfo.InvariantCulture)} fall on an eligible pool of "
                + $"{poolSize} employee(s); at most {fullPackagesPerEmployee} full "
                + $"{AutofillSpecConstants.MaxWorkDays}-day package(s) per employee fit into these {windowDays} "
                + $"day(s) under the 5-work/2-free model, so at most {fullPackageCapacity} shift(s) can lie in "
                + $"full packages and at least {budget} package(s) must be shorter.");
            windows.Add((from, until, budget, cause));
        }

        return windows;
    }
}
