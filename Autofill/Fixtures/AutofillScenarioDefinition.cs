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
