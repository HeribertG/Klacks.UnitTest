// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.ScheduleOptimizer.Models;
using Klacks.UnitTest.Autofill.Fixtures;

namespace Klacks.UnitTest.Autofill.Scenarios.Scenario5;

/// <summary>
/// Builds specification scenario 5: three identically cut orders over March 2026, each carrying a
/// fourth shift 08:00-16:00, seventeen employees in list order, the two previous-month tables of
/// scenario 4 UNCHANGED plus two day-shift packages, five keyword windows and the day-shift preference
/// of the two lowest ranks.
/// <para>
/// THE DAY SHIFT IS A SLOT, NOT A CLASS. The engine has three shift classes and infers them from the
/// time span; 08:00-16:00 lands on LATE. Scenario 5 therefore gives the day shift a shift reference id
/// of its own per order and accepts that every rule about shift kinds counts it as a late shift. A
/// package mixing day and late shifts is not impure from the engine's point of view, and the
/// specification says so explicitly. The consequence to keep in mind while reading any result: the
/// only place a day shift is visible as such is the dayShift metric family, which resolves it through
/// the shift reference id.
/// </para>
/// <para>
/// THE TWO INPUTS ARE NOT EQUALLY STRONG, and the fixture must not pretend otherwise. A blacklist
/// entry is a hard stage-0 veto in the auction; a keyword command is a hard veto in every gate that
/// creates a token; a Preferred entry is a soft term that only tilts the bidding. The day-shift
/// preference of MA-16 and MA-17 can therefore be outvoted by arithmetic at any time, and the
/// specification's own framing agrees: 93 day shifts exist and the two of them can work at most about
/// 22 each, so most day shifts necessarily go to employees who never asked for one.
/// </para>
/// </summary>
public static class Scenario5Fixture
{
    private const int OrderOne = 1;
    private const int OrderTwo = 2;
    private const int OrderThree = 3;

    /// <summary>
    /// The eleven packages still running on 1 March: the nine of scenario 4, unchanged, plus the two
    /// day-shift packages scenario 5 adds. Together they hold eleven of the twelve slots of that day —
    /// every one except the day shift of the third order, which the engine has to staff freshly.
    /// </summary>
    private static readonly Scenario5CarryInRow[] OpenPackages =
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
        new("MA-16", OrderOne, AutofillShiftKind.Day, new DateOnly(2026, 2, 27)),
        new("MA-17", OrderTwo, AutofillShiftKind.Day, new DateOnly(2026, 2, 26)),
    ];

    /// <summary>
    /// The six packages that closed before the period, unchanged from scenario 4, with the shift class
    /// the forward rotation owes them next. Their order is deliberately NOT prescribed: the rotation
    /// table names a class and no order, so the analyzer must not judge one.
    /// </summary>
    private static readonly Scenario5ClosedCarryInRow[] ClosedPackages =
    [
        new("MA-10", OrderOne, AutofillShiftKind.Early, new DateOnly(2026, 2, 28), AutofillShiftKind.Late),
        new("MA-11", OrderOne, AutofillShiftKind.Late, new DateOnly(2026, 2, 27), AutofillShiftKind.Night),
        new("MA-12", OrderTwo, AutofillShiftKind.Night, new DateOnly(2026, 2, 28), AutofillShiftKind.Early),
        new("MA-13", OrderTwo, AutofillShiftKind.Early, new DateOnly(2026, 2, 27), AutofillShiftKind.Late),
        new("MA-14", OrderThree, AutofillShiftKind.Late, new DateOnly(2026, 2, 26), AutofillShiftKind.Night),
        new("MA-15", OrderThree, AutofillShiftKind.Night, new DateOnly(2026, 2, 26), AutofillShiftKind.Early),
    ];

    /// <summary>
    /// The five keyword windows of the specification. These are the engine's OWN per-day schedule
    /// commands and not the qualification keywords scenario 3 simulates through the ban list: FREE
    /// forbids every shift of the day and grants nothing in return — there is no credit path, which is
    /// the deliberate contrast to the holidays of scenario 6 — while the Only* commands narrow the day
    /// to one class without ever forcing an assignment.
    /// </summary>
    private static readonly AutofillScheduleCommand[] KeywordWindows =
    [
        new("MA-01", ScheduleCommandKeyword.Free, new DateOnly(2026, 3, 10), new DateOnly(2026, 3, 12)),
        new("MA-02", ScheduleCommandKeyword.Free, new DateOnly(2026, 3, 17), new DateOnly(2026, 3, 19)),
        new("MA-03", ScheduleCommandKeyword.OnlyEarly, new DateOnly(2026, 3, 18), new DateOnly(2026, 3, 24)),
        new("MA-04", ScheduleCommandKeyword.OnlyLate, new DateOnly(2026, 3, 6), new DateOnly(2026, 3, 10)),
        new("MA-05", ScheduleCommandKeyword.OnlyNight, new DateOnly(2026, 3, 23), new DateOnly(2026, 3, 27)),
    ];

    /// <summary>Assembles the main run S5a: the full fixture with preferences and keyword windows.</summary>
    public static AutofillScenarioDefinition BuildMainRun()
        => BuildCore(withPreferencesAndCommands: true, lockedWorks: null);

    /// <summary>
    /// Assembles the control run S5b: the identical fixture WITHOUT preferences and WITHOUT keyword
    /// windows, so the diff against the main run attributes every difference to one of those two
    /// inputs. Everything else — the day shifts, the roster, the whole previous month including the two
    /// day-shift packages — stays exactly as it is, because a control run that also changed the
    /// carry-in would no longer isolate anything.
    /// </summary>
    public static AutofillScenarioDefinition BuildControlRun()
        => BuildCore(withPreferencesAndCommands: false, lockedWorks: null);

    /// <summary>Assembles the replanning run S5c: the main fixture plus the frozen head of an existing plan.</summary>
    /// <param name="lockedWorks">Result of <c>FrozenPrefix.BuildLockedWorks</c> over the base plan</param>
    public static AutofillScenarioDefinition BuildReplanRun(IReadOnlyList<CoreLockedWork> lockedWorks)
        => BuildCore(withPreferencesAndCommands: true, lockedWorks);

    /// <summary>
    /// The shift preferences of the specification: MA-16 and MA-17 prefer the day shift and are
    /// blacklisted from night duty. Both statements are made once PER ORDER, because the engine binds
    /// a preference to a shift reference id and knows nothing about shift classes — a single entry
    /// would cover one order's night shift and leave the other two wide open.
    /// </summary>
    public static AutofillShiftPreferenceInput BuildPreferences()
    {
        var preferences = new List<AutofillShiftPreference>();
        foreach (var employee in Scenario5SpecConstants.DayShiftEmployees)
        {
            preferences.AddRange(AutofillShiftPreference.ForEveryOrder(
                employee, AutofillShiftKind.Day, ShiftPreferenceKind.Preferred, Scenario5SpecConstants.OrderCount));
            preferences.AddRange(AutofillShiftPreference.ForEveryOrder(
                employee, AutofillShiftKind.Night, ShiftPreferenceKind.Blacklist, Scenario5SpecConstants.OrderCount));
        }

        return new AutofillShiftPreferenceInput(preferences);
    }

    /// <summary>The five keyword windows of the specification, as the fixture states them.</summary>
    public static AutofillScheduleCommandInput BuildScheduleCommands()
        => new(KeywordWindows);

    /// <param name="withPreferencesAndCommands">False to build the control run, which carries neither</param>
    /// <param name="lockedWorks">In-period locked works of a replanning run; null for a fresh start</param>
    private static AutofillScenarioDefinition BuildCore(
        bool withPreferencesAndCommands, IReadOnlyList<CoreLockedWork>? lockedWorks)
    {
        var builder = new AutofillScenarioBuilder()
            .WithPeriod(AutofillSpecConstants.PeriodFrom, AutofillSpecConstants.PeriodUntil)
            .WithOrders(Scenario5SpecConstants.OrderCount)
            .WithDayShiftPerOrder()
            .WithGaParameters(Scenario5GaParameters.PopulationSize, Scenario5GaParameters.MaxGenerations);

        foreach (var employee in Scenario5SpecConstants.Employees)
        {
            builder.AddEmployee(employee, AutofillSpecConstants.GuaranteedHours);
        }

        builder.WithCarryIn([.. BuildCarryIns()]);

        if (withPreferencesAndCommands)
        {
            builder.WithShiftPreferences(BuildPreferences());
            builder.WithScheduleCommands(BuildScheduleCommands());
        }

        if (lockedWorks is not null)
        {
            builder.WithLockedWorks(lockedWorks);
        }

        return builder.Build();
    }

    /// <summary>
    /// The seventeen previous-month packages of the specification: eleven still running on 1 March and
    /// six already closed. Public because scenario 6 is scenario 5 plus holidays and must inherit this
    /// table UNCHANGED — a second fixture stating the same seventeen rows would be a second source of
    /// truth, and the moment the two drifted apart the scenario-6 diff would attribute the drift to
    /// the holiday.
    /// </summary>
    public static IEnumerable<AutofillCarryIn> BuildCarryIns()
    {
        foreach (var row in OpenPackages)
        {
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
                OrderIndex = row.OrderLabel,
                ExpectedFirstOrderIndex = row.OrderLabel,
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
                OrderIndex = row.OrderLabel,
                ExpectedFirstOrderIndex = null,
            };
        }
    }

    /// <summary>A package that closed before the period owes the March plan no further day.</summary>
    private const int ClosedPackageRemainingDays = 0;
}
