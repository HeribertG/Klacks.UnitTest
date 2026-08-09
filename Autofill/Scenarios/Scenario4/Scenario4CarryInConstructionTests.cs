// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Globalization;
using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution.Auction.Agent;
using Klacks.ScheduleOptimizer.TokenEvolution.Auction.Conductor;
using Klacks.ScheduleOptimizer.TokenEvolution.Auction.Controller;
using Klacks.ScheduleOptimizer.TokenEvolution.Initialization;
using Klacks.UnitTest.Autofill.Fixtures;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Autofill.Scenarios.Scenario4;

/// <summary>
/// Stage E1 of the scenario-4 fix plan, measured without an evolution run: the carry-in continuation
/// is now constructed before the free assignment instead of being auctioned, so the detection, the
/// pre-pass and the occupancy skip can all be proven in seconds. A defect found here is a defect of
/// the construction; the minute-long scenario runs then only have to show that the evolution keeps it.
/// </summary>
[TestFixture]
[Category("Autofill")]
[Category("Scenario4")]
public class Scenario4CarryInConstructionTests
{
    private AutofillScenarioDefinition _mainRun = null!;

    [OneTimeSetUp]
    public void BuildTheMainFixture() => _mainRun = Scenario4CarryInFixture.BuildMainRun();

    [Test]
    public void TheFirstPlannableDayOfAWizardStartIsThePeriodStart()
        => CarryInContinuation.FirstPlannableDay(_mainRun.Context)
            .ShouldBe(
                AutofillSpecConstants.PeriodFrom,
                "without in-period locked works nothing is frozen, so the run may staff the period from its "
                + "first day — anything else would silently move the carry-in anchor");

    [Test]
    public void DetectionFindsExactlyTheNineOpenPackagesWithOrderKindAndRemainingDays()
    {
        var detected = CarryInContinuation
            .Detect(_mainRun.Context, AutofillSpecConstants.PeriodFrom)
            .ToDictionary(package => package.AgentId, StringComparer.Ordinal);
        var expected = _mainRun.OpenCarryIns;
        var problems = new List<string>();

        foreach (var carryIn in expected)
        {
            if (!detected.TryGetValue(carryIn.AgentId, out var package))
            {
                problems.Add($"{carryIn.AgentId} was not detected at all");
                continue;
            }

            var expectedShiftId = AutofillShiftCatalog.ShiftIdOf(carryIn.OrderIndex, carryIn.Kind);
            if (package.ShiftRefId != expectedShiftId)
            {
                problems.Add(
                    $"{carryIn.AgentId} was detected on order {AutofillShiftCatalog.OrderOf(package.ShiftRefId)
                        .ToString(CultureInfo.InvariantCulture)} instead of "
                    + carryIn.OrderIndex.ToString(CultureInfo.InvariantCulture));
            }

            if (package.ShiftTypeIndex != AutofillShiftCatalog.ShiftTypeIndexOf(carryIn.Kind))
            {
                problems.Add(
                    $"{carryIn.AgentId} was detected as "
                    + AutofillShiftCatalog.FromShiftTypeIndex(package.ShiftTypeIndex)
                    + $" instead of {carryIn.Kind}");
            }

            if (package.RemainingDays != carryIn.MissingDays)
            {
                problems.Add(
                    $"{carryIn.AgentId} owes {package.RemainingDays.ToString(CultureInfo.InvariantCulture)} day(s) "
                    + $"instead of {carryIn.MissingDays.ToString(CultureInfo.InvariantCulture)}");
            }
        }

        foreach (var package in detected.Values)
        {
            if (expected.All(carryIn => carryIn.AgentId != package.AgentId))
            {
                problems.Add($"{package.AgentId} was detected although its package closed before the period");
            }
        }

        problems.ShouldBeEmpty(
            "the detection must agree with the fixture in all three dimensions — order, shift kind and remaining "
            + "days — because the pre-pass builds on it and A10/S4-7 measures the same three: "
            + string.Join(" | ", problems));
    }

    [Test]
    public void AClosedPackageIsNeverDetected()
    {
        var openEmployees = _mainRun.OpenCarryIns.Select(c => c.AgentId).ToHashSet(StringComparer.Ordinal);
        var closedEmployees = _mainRun.CarryIns
            .Where(c => !openEmployees.Contains(c.AgentId))
            .Select(c => c.AgentId)
            .ToList();

        var detected = CarryInContinuation
            .Detect(_mainRun.Context, AutofillSpecConstants.PeriodFrom)
            .Select(package => package.AgentId)
            .ToHashSet(StringComparer.Ordinal);

        closedEmployees.Where(detected.Contains).ShouldBeEmpty(
            "a package that already reached its length owes the period no day; only the remaining-days boundary "
            + "separates the carry-in construction from the boundary rotation of A13/S4-8, which stays untouched");
    }

    [Test]
    public void AFixtureWithoutAPreviousMonthDetectsNothing()
        => CarryInContinuation.Detect(Scenario4CarryInFixture.BuildWithoutCarryIn().Context)
            .ShouldBeEmpty(
                "without a previous month there is no open package, and the whole construction plus its fitness "
                + "term must then be a no-op — that is what keeps scenarios 1, 1b and S4d unchanged");

    /// <summary>
    /// The replanning seam of F1-d. A frozen head covers every slot up to the day before the replan
    /// date, so the anchor must land ON the replan date — neither earlier, which would let the
    /// construction write into the frozen head, nor later, which would turn the whole construction
    /// into a silent no-op that every S4e assertion would still pass.
    /// </summary>
    [Test]
    public void TheFirstPlannableDayOfAReplanIsTheReplanDate()
    {
        var frozenHead = FrozenPrefix.BuildLockedWorks(
            FullyStaffedPlanUntil(Scenario4SpecConstants.ReplanFrom.AddDays(-1)),
            Scenario4SpecConstants.ReplanFrom);
        var replan = Scenario4CarryInFixture.BuildReplanRun(frozenHead);

        TestContext.Out.WriteLine(
            "frozen head: " + frozenHead.Count.ToString(CultureInfo.InvariantCulture) + " locked work(s) up to "
            + Scenario4SpecConstants.ReplanFrom.AddDays(-1).ToString(
                AutofillSpecConstants.IsoDateFormat, CultureInfo.InvariantCulture));

        CarryInContinuation.FirstPlannableDay(replan.Context).ShouldBe(
            Scenario4SpecConstants.ReplanFrom,
            "a day whose slots are all covered by locked works is frozen and must be skipped, and the first day "
            + "that is not must be the anchor — an anchor before the replan date would let the construction change "
            + "the frozen head, one after it would make the construction a no-op nobody notices");
    }

    /// <summary>
    /// A plan that staffs every slot of every day up to the given day, one employee per slot in
    /// roster order. Stands in for a finished base plan without running the engine.
    /// </summary>
    /// <param name="lastDay">Last day the plan covers</param>
    private CoreScenario FullyStaffedPlanUntil(DateOnly lastDay)
    {
        var tokens = new List<CoreToken>();
        var slotsByDate = _mainRun.Context.Shifts
            .Where(shift => DateOnly.ParseExact(
                shift.Date, AutofillSpecConstants.IsoDateFormat, CultureInfo.InvariantCulture) <= lastDay)
            .GroupBy(shift => shift.Date, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal);

        foreach (var day in slotsByDate)
        {
            var date = DateOnly.ParseExact(day.Key, AutofillSpecConstants.IsoDateFormat, CultureInfo.InvariantCulture);
            var agentIndex = 0;
            foreach (var slot in day.OrderBy(shift => shift.Id, StringComparer.Ordinal))
            {
                var start = TimeOnly.Parse(slot.StartTime, CultureInfo.InvariantCulture);
                var end = TimeOnly.Parse(slot.EndTime, CultureInfo.InvariantCulture);
                tokens.Add(new CoreToken(
                    WorkIds: [],
                    ShiftTypeIndex: ShiftTypeInference.FromSpan(start, end),
                    Date: date,
                    TotalHours: (decimal)slot.Hours,
                    StartAt: date.ToDateTime(start),
                    EndAt: end <= start ? date.AddDays(1).ToDateTime(end) : date.ToDateTime(end),
                    BlockId: Guid.NewGuid(),
                    PositionInBlock: 0,
                    IsLocked: false,
                    LocationContext: null,
                    ShiftRefId: Guid.Parse(slot.Id),
                    AgentId: _mainRun.Context.Agents[agentIndex % _mainRun.Context.Agents.Count].Id));
                agentIndex++;
            }
        }

        return new CoreScenario { Id = Guid.NewGuid().ToString(), Tokens = tokens };
    }

    [Test]
    public void ThePrePassPlacesEveryOwedDayOnItsOwnOrderAndKind()
    {
        var tokens = new List<CoreToken>();
        var seed = CarryInContinuationSeeder.Seed(_mainRun.Context, tokens);
        var placed = seed.Placed
            .Select(token => (token.AgentId, token.Date, token.ShiftRefId))
            .ToHashSet();
        var problems = new List<string>();
        var owed = 0;

        foreach (var carryIn in _mainRun.OpenCarryIns)
        {
            var shiftId = AutofillShiftCatalog.ShiftIdOf(carryIn.OrderIndex, carryIn.Kind);
            for (var offset = 0; offset < carryIn.MissingDays; offset++)
            {
                owed++;
                var day = AutofillSpecConstants.PeriodFrom.AddDays(offset);
                if (!placed.Contains((carryIn.AgentId, day, shiftId)))
                {
                    problems.Add($"{carryIn.AgentId} holds no {carryIn.Kind} of order "
                        + carryIn.OrderIndex.ToString(CultureInfo.InvariantCulture)
                        + $" on {day:yyyy-MM-dd}");
                }
            }
        }

        TestContext.Out.WriteLine(
            "pre-pass placed " + seed.Placed.Count.ToString(CultureInfo.InvariantCulture)
            + " token(s) for " + owed.ToString(CultureInfo.InvariantCulture) + " owed day(s)");

        problems.ShouldBeEmpty(
            "the day-major pre-pass must serve every owed day of every open package on the very order and kind the "
            + "package ran on; the nine packages of the first day address nine disjoint slots, so no competition "
            + "can excuse a miss: " + string.Join(" | ", problems));
        seed.Placed.Count.ShouldBe(owed);
    }

    [Test]
    public void TheAuctionNeverSellsASlotTheConstructionAlreadyHolds()
    {
        var auctioneer = new SlotAuctioneer(
            new FuzzyBiddingAgent(), new Stage0HardConstraintChecker(), new Stage1SoftConstraintChecker());
        var outcome = auctioneer.Run(_mainRun.Context, new Random(_mainRun.Config.RandomSeed));

        var overstaffed = outcome.Scenario.Tokens
            .GroupBy(token => (token.Date, token.ShiftRefId))
            .Where(group => group.Count() > 1)
            .ToList();
        var doubleBooked = outcome.Scenario.Tokens
            .GroupBy(token => (token.AgentId, token.Date))
            .Where(group => group.Count() > 1)
            .ToList();

        TestContext.Out.WriteLine(
            "auction seed: " + outcome.Scenario.Tokens.Count.ToString(CultureInfo.InvariantCulture)
            + " token(s), " + outcome.Results.Count.ToString(CultureInfo.InvariantCulture)
            + " slot(s) auctioned of " + _mainRun.Context.Shifts.Count.ToString(CultureInfo.InvariantCulture));

        overstaffed.ShouldBeEmpty(
            "a slot that already carries its demand must not be auctioned again — one place, one employee: "
            + string.Join(" | ", overstaffed.Select(g => $"{g.Key.Date:yyyy-MM-dd} {g.Key.ShiftRefId} x{g.Count()}")));
        doubleBooked.ShouldBeEmpty(
            "the construction must not put an employee on two slots of the same day: "
            + string.Join(" | ", doubleBooked.Select(g => $"{g.Key.AgentId} {g.Key.Date:yyyy-MM-dd}")));
    }

    [Test]
    public void TheAuctionSeedContinuesEveryOpenPackage()
    {
        var auctioneer = new SlotAuctioneer(
            new FuzzyBiddingAgent(), new Stage0HardConstraintChecker(), new Stage1SoftConstraintChecker());
        var outcome = auctioneer.Run(_mainRun.Context, new Random(_mainRun.Config.RandomSeed));
        var held = outcome.Scenario.Tokens
            .Select(token => (token.AgentId, token.Date, token.ShiftRefId))
            .ToHashSet();

        var missing = new List<string>();
        foreach (var carryIn in _mainRun.OpenCarryIns)
        {
            var shiftId = AutofillShiftCatalog.ShiftIdOf(carryIn.OrderIndex, carryIn.Kind);
            for (var offset = 0; offset < carryIn.MissingDays; offset++)
            {
                var day = AutofillSpecConstants.PeriodFrom.AddDays(offset);
                if (!held.Contains((carryIn.AgentId, day, shiftId)))
                {
                    missing.Add($"{carryIn.AgentId} {day:yyyy-MM-dd}");
                }
            }
        }

        missing.ShouldBeEmpty(
            "the auction seed plan is the input of A10/S4-7 and it must now carry every open package to its end: "
            + string.Join(", ", missing));
    }
}
