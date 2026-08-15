// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.ScheduleOptimizer.Models;

namespace Klacks.UnitTest.Autofill.Fixtures;

/// <summary>
/// One master-data shift preference of one employee, expressed the way a fixture thinks — order and
/// slot kind — rather than the way the engine stores it, which is a bare shift reference id.
/// <para>
/// The engine binds a preference to a SHIFT REFERENCE ID, never to a shift class:
/// <c>CoreShiftPreference(AgentId, ShiftRefId, Kind)</c>, checked in
/// <c>Stage0HardConstraintChecker.IsBlacklistedShift</c> by parsing the slot id and comparing Guids.
/// "All night shifts" is therefore not one entry but one entry per order, which is what
/// <see cref="ForEveryOrder"/> exists for — writing it as a single entry would blacklist one order's
/// night shift and quietly leave the other two open.
/// </para>
/// </summary>
/// <param name="AgentId">Employee the preference belongs to</param>
/// <param name="OrderIndex">Order the shift belongs to</param>
/// <param name="Kind">Slot kind of the shift; the day shift is addressable here, unlike by class</param>
/// <param name="Preference">Preferred (soft, motivation) or Blacklist (hard veto in stage 0)</param>
public sealed record AutofillShiftPreference(
    string AgentId,
    int OrderIndex,
    AutofillShiftKind Kind,
    ShiftPreferenceKind Preference)
{
    /// <summary>Shift reference id this preference binds to — the value the engine compares.</summary>
    public Guid ShiftRefId => AutofillShiftCatalog.ShiftIdOf(OrderIndex, Kind);

    /// <summary>Translates the fixture entry into the engine's own record.</summary>
    public CoreShiftPreference ToCore() => new(AgentId, ShiftRefId, Preference);

    /// <summary>
    /// The same preference on one slot kind of every order — the only faithful way to express a
    /// statement like "this employee is blacklisted from night duty" against an engine that knows
    /// shift ids and not shift classes.
    /// </summary>
    /// <param name="agentId">Employee the preference belongs to</param>
    /// <param name="kind">Slot kind the preference addresses</param>
    /// <param name="preference">Preferred or Blacklist</param>
    /// <param name="orderCount">Number of parallel orders the scenario plans</param>
    public static IEnumerable<AutofillShiftPreference> ForEveryOrder(
        string agentId, AutofillShiftKind kind, ShiftPreferenceKind preference, int orderCount)
    {
        for (var order = AutofillShiftCatalog.FirstOrderIndex;
             order < AutofillShiftCatalog.FirstOrderIndex + orderCount;
             order++)
        {
            yield return new AutofillShiftPreference(agentId, order, kind, preference);
        }
    }
}
