// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution;

namespace Klacks.UnitTest.Autofill.Fixtures;

/// <summary>
/// Everything a scenario test needs to run and to interpret one autofill run: the engine input, the
/// engine configuration and the declarative facts the analyzer cannot recover from the plan alone.
/// </summary>
/// <param name="Context">Engine input; its Agents order IS the roster priority order</param>
/// <param name="Config">Engine configuration, already pinned for determinism</param>
/// <param name="EmployeesInListOrder">Employee ids, index 0 = list rank 1</param>
/// <param name="CarryIns">Previous-month packages and their expectations; empty for a clean start</param>
/// <param name="CarryInFrom">Earliest carry-in day, or null when there is no carry-in</param>
public sealed record AutofillScenarioDefinition(
    CoreWizardContext Context,
    TokenEvolutionConfig Config,
    IReadOnlyList<string> EmployeesInListOrder,
    IReadOnlyList<AutofillCarryIn> CarryIns,
    DateOnly? CarryInFrom)
{
    /// <summary>
    /// Hours each employee already worked in the carry-in month. Always reported, but only handed to
    /// the engine as <c>CoreAgent.CurrentHours</c> when
    /// <see cref="CarryInHoursCountedAsCurrentHours"/> is true.
    /// </summary>
    public IReadOnlyDictionary<string, double> CarryInHoursByAgent { get; init; }
        = new Dictionary<string, double>(StringComparer.Ordinal);

    /// <summary>True when the carry-in hours were handed to the engine as CurrentHours.</summary>
    public bool CarryInHoursCountedAsCurrentHours { get; init; }

    /// <summary>
    /// The eligibility knowledge of the scenario, when it has any: the same ban list the engine
    /// context carries, plus the fixture's kind map and keyword facts the analyzer needs to report
    /// pools and violations. Null for a scenario without keyword restrictions — the analyzer then
    /// leaves every eligibility-derived metric empty, so scenarios 1, 1b and 2 measure exactly as
    /// before.
    /// </summary>
    public AutofillEligibilityInput? Eligibility { get; init; }

    /// <summary>
    /// The master-data shift preferences of the scenario, when it has any — the same list the engine
    /// context carries. Null for a scenario without preferences, whose preference-derived metrics then
    /// stay empty.
    /// </summary>
    public AutofillShiftPreferenceInput? ShiftPreferences { get; init; }

    /// <summary>
    /// The per-day planning commands of the scenario, when it has any — the same windows the engine
    /// context carries, there expanded to one row per day. Null for a scenario without commands.
    /// </summary>
    public AutofillScheduleCommandInput? ScheduleCommands { get; init; }

    /// <summary>
    /// The booked absences of the scenario, when it has any — the same one-day rows the engine context
    /// carries as <c>BreakBlockers</c>. Null for a scenario without absences, whose absence-derived
    /// metrics then stay empty, so scenarios 1 to 5 measure exactly what they measured before.
    /// </summary>
    public AutofillBreakBlockerInput? BreakBlockers { get; init; }

    /// <summary>The absences of the scenario, never null — an empty input for a scenario without any.</summary>
    public AutofillBreakBlockerInput Absences => BreakBlockers ?? AutofillBreakBlockerInput.Empty;

    /// <summary>True when at least one employee is absent on at least one day of the period.</summary>
    public bool HasAbsences => BreakBlockers is { IsEmpty: false };

    /// <summary>
    /// Number of parallel orders the scenario plans. 1 for every scenario that never called
    /// <c>WithOrders</c>; see <see cref="OrdersAreLabelled"/> for the difference between "one order"
    /// and "no order dimension".
    /// </summary>
    public int OrderCount { get; init; } = 1;

    /// <summary>
    /// True when the scenario addressed its shifts through the order-aware catalog, so every metric
    /// may report an order index. False for the order-less scenarios, whose shift ids carry
    /// <see cref="AutofillShiftCatalog.SingleOrderIndex"/> and whose order metrics stay empty.
    /// </summary>
    public bool OrdersAreLabelled { get; init; }

    /// <summary>
    /// True when every order carries a fourth shift 08:00-16:00 with an id of its own. It changes how
    /// many slots a day has, which is why the two counts below are derived from it rather than from
    /// the suite-wide shift count.
    /// </summary>
    public bool HasDayShifts { get; init; }

    /// <summary>Slots one order demands per day: three, or four with a day shift.</summary>
    public int SlotsPerDayPerOrder => AutofillShiftCatalog.SlotKindsOf(HasDayShifts).Count;

    /// <summary>
    /// Slots the whole scenario demands per day, over all orders. Derived from the scenario rather
    /// than read from a shared constant, because the shared shift count describes the three-shift cut
    /// and is the definition of the other scenarios' expectations.
    /// </summary>
    public int SlotsPerDay => SlotsPerDayPerOrder * OrderCount;

    /// <summary>First day of the planning period.</summary>
    public DateOnly PeriodFrom => Context.PeriodFrom;

    /// <summary>Last day of the planning period, inclusive.</summary>
    public DateOnly PeriodUntil => Context.PeriodUntil;

    /// <summary>Carry-in packages that are still running on the first day of the period.</summary>
    public IReadOnlyList<AutofillCarryIn> OpenCarryIns
        => CarryIns.Where(c => c.IsOpenAt(PeriodFrom)).ToList();

    /// <summary>List rank of an employee, 1-based; 0 when the employee is unknown.</summary>
    /// <param name="agentId">Employee identifier</param>
    public int ListRankOf(string agentId)
    {
        for (var i = 0; i < EmployeesInListOrder.Count; i++)
        {
            if (string.Equals(EmployeesInListOrder[i], agentId, StringComparison.Ordinal))
            {
                return i + 1;
            }
        }

        return 0;
    }
}
