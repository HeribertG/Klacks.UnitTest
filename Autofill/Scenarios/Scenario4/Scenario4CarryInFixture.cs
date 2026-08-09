// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.ScheduleOptimizer.Models;
using Klacks.UnitTest.Autofill.Fixtures;

namespace Klacks.UnitTest.Autofill.Scenarios.Scenario4;

/// <summary>
/// Builds specification scenario 4: three identically cut orders over March 2026, fifteen employees in
/// list order, and the two previous-month tables of the specification — nine employees still inside a
/// package that covers all nine (order, shift kind) combinations of 1 March exactly once, and six
/// employees whose package closed on staggered February days.
/// <para>
/// The three orders differ on engine level by their shift ids and by nothing else. That is not a
/// simplification of the fixture but the whole truth about the engine: <c>CoreShift</c> has no order,
/// root or parent field, so three identically cut orders are three id triples. Container shifts are
/// the wrong tool here — the wizard never expands them — and one shift with a quantity of three is
/// worse still, because the evaluation context would collapse the three into a single capacity-three
/// slot and the order dimension would be gone before the first generation.
/// </para>
/// </summary>
public static class Scenario4CarryInFixture
{
    private const int OrderOne = 1;
    private const int OrderTwo = 2;
    private const int OrderThree = 3;

    /// <summary>
    /// The nine packages still running on 1 March. Order and shift kind together address one slot, and
    /// the nine entries cover the nine slots of that day exactly once; the served days decide how far
    /// each package still reaches into March.
    /// </summary>
    private static readonly Scenario4CarryInRow[] OpenPackages =
    [
        new("MA-01", OrderOne, AutofillShiftKind.Early, new DateOnly(2026, 2, 25)),
        new("MA-02", OrderOne, AutofillShiftKind.Late, new DateOnly(2026, 2, 26)),
        new("MA-03", OrderOne, AutofillShiftKind.Night, new DateOnly(2026, 2, 27)),
        new("MA-04", OrderTwo, AutofillShiftKind.Early, new DateOnly(2026, 2, 28)),
        new("MA-05", OrderTwo, AutofillShiftKind.Late, new DateOnly(2026, 2, 25)),
        new("MA-06", OrderTwo, AutofillShiftKind.Night, new DateOnly(2026, 2, 26)),
        new("MA-07", OrderThree, AutofillShiftKind.Early, new DateOnly(2026, 2, 27)),
        new("MA-08", OrderThree, AutofillShiftKind.Late, new DateOnly(2026, 2, 28)),
        new("MA-09", OrderThree, AutofillShiftKind.Night, new DateOnly(2026, 2, 25)),
    ];

    /// <summary>
    /// The six packages that closed before the period, with the shift kind the forward rotation owes
    /// them next. Their order is deliberately NOT prescribed: the specification's rotation table names
    /// a kind and no order, so the analyzer must not judge one.
    /// </summary>
    private static readonly Scenario4ClosedCarryInRow[] ClosedPackages =
    [
        new("MA-10", OrderOne, AutofillShiftKind.Early, new DateOnly(2026, 2, 28), AutofillShiftKind.Late),
        new("MA-11", OrderOne, AutofillShiftKind.Late, new DateOnly(2026, 2, 27), AutofillShiftKind.Night),
        new("MA-12", OrderTwo, AutofillShiftKind.Night, new DateOnly(2026, 2, 28), AutofillShiftKind.Early),
        new("MA-13", OrderTwo, AutofillShiftKind.Early, new DateOnly(2026, 2, 27), AutofillShiftKind.Late),
        new("MA-14", OrderThree, AutofillShiftKind.Late, new DateOnly(2026, 2, 26), AutofillShiftKind.Night),
        new("MA-15", OrderThree, AutofillShiftKind.Night, new DateOnly(2026, 2, 26), AutofillShiftKind.Early),
    ];

    /// <summary>Assembles the main run: full carry-in, 180 guaranteed hours, orders in their natural order.</summary>
    public static AutofillScenarioDefinition BuildMainRun()
        => Build(AutofillSpecConstants.GuaranteedHours, includeCarryIn: true, swapOuterOrders: false, lockedWorks: null);

    /// <summary>Assembles the calibration run: same fixture at 150 guaranteed hours.</summary>
    public static AutofillScenarioDefinition BuildCalibrationRun()
        => Build(
            AutofillSpecConstants.CalibrationGuaranteedHours,
            includeCarryIn: true,
            swapOuterOrders: false,
            lockedWorks: null);

    /// <summary>
    /// Assembles the symmetry run: the first and the third order exchange their id triples and the
    /// shifts are appended back to front, while the carry-in stays bound to its labels. The engine
    /// sorts its slots totally, so a pure change of insertion order cannot matter; exchanging the ids
    /// is the part that could, because with identical dates and start times the id IS the auction's
    /// sort key. That is exactly why the comparison against the main run has to be label-invariant.
    /// </summary>
    public static AutofillScenarioDefinition BuildSymmetryRun()
        => Build(AutofillSpecConstants.GuaranteedHours, includeCarryIn: true, swapOuterOrders: true, lockedWorks: null);

    /// <summary>Assembles the run without a previous month — the diagnostic counterpart of the main run.</summary>
    public static AutofillScenarioDefinition BuildWithoutCarryIn()
        => Build(AutofillSpecConstants.GuaranteedHours, includeCarryIn: false, swapOuterOrders: false, lockedWorks: null);

    /// <summary>
    /// Assembles the replanning run: the main fixture plus the frozen head of an existing plan.
    /// </summary>
    /// <param name="lockedWorks">Result of <c>FrozenPrefix.BuildLockedWorks</c> over the base plan</param>
    public static AutofillScenarioDefinition BuildReplanRun(IReadOnlyList<CoreLockedWork> lockedWorks)
        => Build(AutofillSpecConstants.GuaranteedHours, includeCarryIn: true, swapOuterOrders: false, lockedWorks);

    /// <summary>
    /// Assembles the timing probe: the carry-in-free fixture at the unmodified suite GA size, so the
    /// measured wall clock says what a scenario-4 run costs before any budget decision.
    /// </summary>
    public static AutofillScenarioDefinition BuildTimingProbe()
        => BuildCore(
            AutofillSpecConstants.GuaranteedHours,
            includeCarryIn: false,
            swapOuterOrders: false,
            lockedWorks: null,
            populationSize: AutofillSpecConstants.PopulationSize,
            maxGenerations: AutofillSpecConstants.MaxGenerations);

    /// <param name="guaranteedHours">Guaranteed hours of every employee</param>
    /// <param name="includeCarryIn">True to add the two previous-month tables</param>
    /// <param name="swapOuterOrders">True to give order 1 the id triple of order 3 and the other way round</param>
    /// <param name="lockedWorks">In-period locked works of a replanning run; null for a fresh start</param>
    public static AutofillScenarioDefinition Build(
        double guaranteedHours,
        bool includeCarryIn,
        bool swapOuterOrders,
        IReadOnlyList<CoreLockedWork>? lockedWorks)
        => BuildCore(
            guaranteedHours,
            includeCarryIn,
            swapOuterOrders,
            lockedWorks,
            Scenario4GaParameters.PopulationSize,
            Scenario4GaParameters.MaxGenerations);

    /// <summary>
    /// Id triple one order label uses. Without the swap a label addresses its own triple; with it the
    /// two outer labels exchange triples while the middle one keeps its own.
    /// </summary>
    /// <param name="orderLabel">Order label of the specification tables, 1 to 3</param>
    /// <param name="swapOuterOrders">True to exchange the triples of the first and the third order</param>
    public static int SlotOf(int orderLabel, bool swapOuterOrders)
    {
        if (!swapOuterOrders)
        {
            return orderLabel;
        }

        return orderLabel switch
        {
            OrderOne => OrderThree,
            OrderThree => OrderOne,
            _ => orderLabel,
        };
    }

    private static AutofillScenarioDefinition BuildCore(
        double guaranteedHours,
        bool includeCarryIn,
        bool swapOuterOrders,
        IReadOnlyList<CoreLockedWork>? lockedWorks,
        int populationSize,
        int maxGenerations)
    {
        var builder = new AutofillScenarioBuilder()
            .WithPeriod(AutofillSpecConstants.PeriodFrom, AutofillSpecConstants.PeriodUntil)
            .WithOrders(Scenario4SpecConstants.OrderCount, swapOuterOrders)
            .WithGaParameters(populationSize, maxGenerations);

        foreach (var employee in Scenario4SpecConstants.Employees)
        {
            builder.AddEmployee(employee, guaranteedHours);
        }

        if (includeCarryIn)
        {
            builder.WithCarryIn([.. BuildCarryIns(swapOuterOrders)]);
        }

        if (lockedWorks is not null)
        {
            builder.WithLockedWorks(lockedWorks);
        }

        return builder.Build();
    }

    private static IEnumerable<AutofillCarryIn> BuildCarryIns(bool swapOuterOrders)
    {
        foreach (var row in OpenPackages)
        {
            var slot = SlotOf(row.OrderLabel, swapOuterOrders);
            var served = AutofillSpecConstants.CarryInMonthUntil.DayNumber - row.PackageStart.DayNumber + 1;
            yield return new AutofillCarryIn(
                row.AgentId,
                row.Kind,
                row.PackageStart,
                AutofillSpecConstants.CarryInMonthUntil,
                AutofillSpecConstants.MaxWorkDays,
                row.Kind,
                AutofillSpecConstants.MaxWorkDays - served)
            {
                OrderIndex = slot,
                ExpectedFirstOrderIndex = slot,
            };
        }

        foreach (var row in ClosedPackages)
        {
            yield return new AutofillCarryIn(
                row.AgentId,
                row.Kind,
                row.PackageEndInclusive.AddDays(1 - AutofillSpecConstants.MaxWorkDays),
                row.PackageEndInclusive,
                AutofillSpecConstants.MaxWorkDays,
                row.NextKind,
                ClosedPackageRemainingDays)
            {
                OrderIndex = SlotOf(row.OrderLabel, swapOuterOrders),
                ExpectedFirstOrderIndex = null,
            };
        }
    }

    /// <summary>A package that closed before the period owes the March plan no further day.</summary>
    private const int ClosedPackageRemainingDays = 0;
}
