// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution;

namespace Klacks.UnitTest.Autofill.Fixtures;

/// <summary>
/// Fluent builder for the autofill scenarios. Produces the engine input (<see cref="CoreWizardContext"/>)
/// and the engine configuration (<see cref="TokenEvolutionConfig"/>) for a run of
/// <c>TokenEvolutionLoop.Run</c> — no database, no DI, no clock.
/// <para>
/// Two things the builder deliberately does explicitly rather than by fallback. First, every contract
/// value the rules depend on (MaxWorkDays, MinRestDays, MinRestHours, MaxConsecutiveDays, daily and
/// weekly caps) is set on the agent, because a fixture that relies on a default proves nothing about
/// which value the engine actually used. Second, the previous month is modelled as
/// <c>BoundaryLockedWorks</c>: that is the one collection the engine reads for work that lies before
/// the period — it seeds the auction's per-agent runtime state and feeds the consecutive-day and
/// rest-time validators, but it is never planned, never scored and never mutated.
/// </para>
/// </summary>
public sealed class AutofillScenarioBuilder
{
    private const string BoundaryWorkIdPrefix = "carry-in-";

    private const int SingleOrderCount = 1;

    private readonly List<AutofillEmployeeSpec> _employees = [];
    private readonly List<AutofillCarryIn> _carryIns = [];

    private DateOnly _periodFrom = AutofillSpecConstants.PeriodFrom;
    private DateOnly _periodUntil = AutofillSpecConstants.PeriodUntil;
    private int _randomSeed = AutofillSpecConstants.RandomSeed;
    private IReadOnlyList<CoreLockedWork>? _lockedWorks;
    private int _populationSize = AutofillSpecConstants.PopulationSize;
    private int _maxGenerations = AutofillSpecConstants.MaxGenerations;
    private bool _carryInHoursCountAsCurrentHours;
    private AutofillEligibilityInput? _eligibility;
    private int? _orderCount;
    private bool _reverseShiftInsertionOrder;

    /// <param name="from">First day of the planning period</param>
    /// <param name="until">Last day of the planning period, inclusive</param>
    public AutofillScenarioBuilder WithPeriod(DateOnly from, DateOnly until)
    {
        if (until < from)
        {
            throw new ArgumentException("Period end must not lie before the period start.", nameof(until));
        }

        _periodFrom = from;
        _periodUntil = until;
        return this;
    }

    /// <summary>
    /// Adds <paramref name="count"/> employees named MA-1 .. MA-n, in that list order, all with the
    /// same guaranteed hours.
    /// </summary>
    /// <param name="count">Number of employees</param>
    /// <param name="guaranteedHours">Guaranteed hours of every one of them</param>
    public AutofillScenarioBuilder WithEmployees(int count, double guaranteedHours)
    {
        if (count <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, "A scenario needs at least one employee.");
        }

        for (var rank = 1; rank <= count; rank++)
        {
            AddEmployee(AutofillSpecConstants.EmployeeIdPrefix + rank, guaranteedHours);
        }

        return this;
    }

    /// <summary>Appends one employee at the end of the list, i.e. at the next list rank.</summary>
    /// <param name="id">Employee identifier</param>
    /// <param name="guaranteedHours">Guaranteed hours of this employee</param>
    public AutofillScenarioBuilder AddEmployee(string id, double guaranteedHours)
    {
        if (_employees.Any(e => string.Equals(e.Id, id, StringComparison.Ordinal)))
        {
            throw new ArgumentException($"Employee '{id}' is already part of the scenario.", nameof(id));
        }

        _employees.Add(new AutofillEmployeeSpec(id, guaranteedHours));
        return this;
    }

    /// <summary>Adds previous-month packages that reach into the planning period.</summary>
    /// <param name="carryIns">One package per employee; employees without one simply start free</param>
    public AutofillScenarioBuilder WithCarryIn(params AutofillCarryIn[] carryIns)
    {
        _carryIns.AddRange(carryIns);
        return this;
    }

    /// <summary>
    /// Counts the hours of the previous-month packages towards <c>CoreAgent.CurrentHours</c>.
    /// Off by default: the production context builder fills CurrentHours from a separate mechanism
    /// (the hours carry-in over the lookback window), and switching it on changes how much the
    /// fitness still wants to plan in the period, which would make the hour assertions of scenario 2
    /// incomparable to scenario 1. The computed value is always reported on the definition.
    /// </summary>
    public AutofillScenarioBuilder WithCarryInHoursAsCurrentHours()
    {
        _carryInHoursCountAsCurrentHours = true;
        return this;
    }

    /// <summary>
    /// Adds the scenario's eligibility restriction. One input feeds both sides of the run: the ban
    /// list becomes <c>CoreWizardContext.IneligibleAssignments</c> — the engine's only eligibility
    /// field — and the whole input lands on the definition for the analyzer, so the metrics can never
    /// measure against a different ban list than the one the engine planned with.
    /// </summary>
    /// <param name="eligibility">Ban list, shift-kind map and keyword facts of the scenario</param>
    public AutofillScenarioBuilder WithEligibility(AutofillEligibilityInput eligibility)
    {
        _eligibility = eligibility;
        return this;
    }

    /// <summary>
    /// Adds in-period locked works the engine must keep immutable — the frozen-prefix replanning
    /// input: pass the result of <c>FrozenPrefix.BuildLockedWorks</c> to freeze an existing plan
    /// up to a replan date.
    /// </summary>
    /// <param name="lockedWorks">Locked works that become immutable genome tokens</param>
    public AutofillScenarioBuilder WithLockedWorks(IReadOnlyList<CoreLockedWork> lockedWorks)
    {
        _lockedWorks = lockedWorks;
        return this;
    }

    /// <summary>
    /// Plans several identically cut orders in parallel instead of a single one: every day then holds
    /// three shifts per order, each an independent slot with its own pinned id. Not calling this at
    /// all keeps the order-less shift triple of scenarios 1, 1b, 2 and 3, which is a different set of
    /// ids — the order dimension is opt-in, never a relabelling of the existing one.
    /// </summary>
    /// <param name="count">Number of parallel orders, 1 to <see cref="AutofillShiftCatalog.MaxOrderCount"/></param>
    /// <param name="reverseInsertionOrder">
    /// True to append the orders back to front. The engine sorts its slots totally, so this changes
    /// nothing about the run; it exists so a symmetry test can prove that instead of assuming it
    /// </param>
    public AutofillScenarioBuilder WithOrders(int count, bool reverseInsertionOrder = false)
    {
        _orderCount = count;
        _reverseShiftInsertionOrder = reverseInsertionOrder;
        return this;
    }

    /// <param name="seed">Seed of the single random source of the run</param>
    public AutofillScenarioBuilder WithRandomSeed(int seed)
    {
        _randomSeed = seed;
        return this;
    }

    /// <summary>
    /// Overrides the genetic-algorithm size. Early stopping is always pinned to
    /// <paramref name="maxGenerations"/> so a run ends on a generation count, never on a wall clock.
    /// </summary>
    /// <param name="populationSize">Scenarios per generation</param>
    /// <param name="maxGenerations">Generation cap</param>
    public AutofillScenarioBuilder WithGaParameters(int populationSize, int maxGenerations)
    {
        _populationSize = populationSize;
        _maxGenerations = maxGenerations;
        return this;
    }

    /// <summary>
    /// Assembles the scenario. Fails loudly when the shift-type inference no longer classifies the
    /// suite's three time spans as early/late/night — every rule about shift kinds would silently
    /// measure something else.
    /// </summary>
    public AutofillScenarioDefinition Build()
    {
        var inferenceProblems = AutofillShiftCatalog.ValidateShiftTypeInference();
        if (inferenceProblems.Count > 0)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, inferenceProblems));
        }

        if (_orderCount.HasValue)
        {
            var orderingProblems = AutofillShiftCatalog.ValidateOrderIdOrdering();
            if (orderingProblems.Count > 0)
            {
                throw new InvalidOperationException(string.Join(Environment.NewLine, orderingProblems));
            }
        }

        if (_employees.Count == 0)
        {
            throw new InvalidOperationException("A scenario needs at least one employee; call WithEmployees or AddEmployee.");
        }

        var unknownCarryIn = _carryIns
            .FirstOrDefault(c => !_employees.Any(e => string.Equals(e.Id, c.AgentId, StringComparison.Ordinal)));
        if (unknownCarryIn is not null)
        {
            throw new InvalidOperationException(
                $"Carry-in refers to employee '{unknownCarryIn.AgentId}', who is not part of the scenario.");
        }

        var carryInHours = BuildCarryInHours();
        var boundaryWorks = BuildBoundaryWorks();

        var context = new CoreWizardContext
        {
            PeriodFrom = _periodFrom,
            PeriodUntil = _periodUntil,
            Agents = _employees.Select(e => BuildAgent(e, carryInHours)).ToList(),
            Shifts = BuildShifts(),
            BoundaryLockedWorks = boundaryWorks,
            LockedWorks = _lockedWorks ?? [],
            SchedulingMaxConsecutiveDays = AutofillSpecConstants.MaxConsecutiveDays,
            SchedulingMinPauseHours = AutofillSpecConstants.MinRestHours,
            SchedulingMaxOptimalGap = AutofillSpecConstants.MaxOptimalGap,
            SchedulingMaxDailyHours = AutofillSpecConstants.MaxDailyHours,
            SchedulingMaxWeeklyHours = AutofillSpecConstants.MaxWeeklyHours,
            IneligibleAssignments = _eligibility?.IneligibleAssignments
                ?? new HashSet<(string, Guid, DateOnly)>(),
        };

        if (_eligibility is not null)
        {
            var eligibilityProblems = _eligibility.ValidationProblems(
                context, _employees.Select(e => e.Id).ToList());
            if (eligibilityProblems.Count > 0)
            {
                throw new InvalidOperationException(string.Join(Environment.NewLine, eligibilityProblems));
            }
        }

        var config = new TokenEvolutionConfig
        {
            PopulationSize = _populationSize,
            MaxGenerations = _maxGenerations,
            EarlyStopNoImprovementGenerations = _maxGenerations,
            RandomSeed = _randomSeed,
            EvaluationParallelism = AutofillSpecConstants.EvaluationParallelism,
            MaxRuntime = null,
        };

        return new AutofillScenarioDefinition(
            Context: context,
            Config: config,
            EmployeesInListOrder: _employees.Select(e => e.Id).ToList(),
            CarryIns: _carryIns.ToList(),
            CarryInFrom: _carryIns.Count == 0 ? null : _carryIns.Min(c => c.PackageStart))
        {
            CarryInHoursByAgent = carryInHours,
            CarryInHoursCountedAsCurrentHours = _carryInHoursCountAsCurrentHours,
            Eligibility = _eligibility,
            OrderCount = _orderCount ?? SingleOrderCount,
            OrdersAreLabelled = _orderCount.HasValue,
        };
    }

    private IReadOnlyList<CoreShift> BuildShifts()
    {
        if (!_orderCount.HasValue)
        {
            return AutofillShiftCatalog.BuildDailyShifts(_periodFrom, _periodUntil);
        }

        var shifts = AutofillShiftCatalog.BuildDailyShifts(_periodFrom, _periodUntil, _orderCount.Value);
        if (!_reverseShiftInsertionOrder)
        {
            return shifts;
        }

        var reversed = shifts.ToList();
        reversed.Reverse();
        return reversed;
    }

    private Dictionary<string, double> BuildCarryInHours()
    {
        var hours = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var carryIn in _carryIns)
        {
            var served = carryIn.Days().Count(d => d < _periodFrom);
            hours.TryGetValue(carryIn.AgentId, out var current);
            hours[carryIn.AgentId] = current + (served * AutofillSpecConstants.ShiftHours);
        }

        return hours;
    }

    private List<CoreLockedWork> BuildBoundaryWorks()
    {
        var works = new List<CoreLockedWork>();
        foreach (var carryIn in _carryIns.OrderBy(c => c.AgentId, StringComparer.Ordinal))
        {
            foreach (var date in carryIn.Days())
            {
                if (date >= _periodFrom)
                {
                    throw new InvalidOperationException(
                        $"Carry-in of employee '{carryIn.AgentId}' contains {date:yyyy-MM-dd}, which is not before "
                        + $"the period start {_periodFrom:yyyy-MM-dd}. Carry-in days must lie in the previous month.");
                }

                var (startAt, endAt) = AutofillShiftCatalog.SpanOf(carryIn.Kind, date);
                works.Add(new CoreLockedWork(
                    WorkId: $"{BoundaryWorkIdPrefix}{carryIn.AgentId}-{date:yyyyMMdd}",
                    AgentId: carryIn.AgentId,
                    Date: date,
                    ShiftTypeIndex: AutofillShiftCatalog.ShiftTypeIndexOf(carryIn.Kind),
                    TotalHours: (decimal)AutofillSpecConstants.ShiftHours,
                    StartAt: startAt,
                    EndAt: endAt,
                    ShiftRefId: AutofillShiftCatalog.ShiftIdOf(carryIn.OrderIndex, carryIn.Kind),
                    LocationContext: null));
            }
        }

        return works;
    }

    private CoreAgent BuildAgent(AutofillEmployeeSpec employee, IReadOnlyDictionary<string, double> carryInHours)
    {
        var currentHours = _carryInHoursCountAsCurrentHours && carryInHours.TryGetValue(employee.Id, out var hours)
            ? hours
            : AutofillSpecConstants.CurrentHours;

        return new CoreAgent(
            Id: employee.Id,
            CurrentHours: currentHours,
            GuaranteedHours: employee.GuaranteedHours,
            MaxConsecutiveDays: AutofillSpecConstants.MaxConsecutiveDays,
            MinRestHours: AutofillSpecConstants.MinRestHours,
            Motivation: AutofillSpecConstants.Motivation,
            MaxDailyHours: AutofillSpecConstants.MaxDailyHours,
            MaxWeeklyHours: AutofillSpecConstants.MaxWeeklyHours,
            MaxOptimalGap: AutofillSpecConstants.MaxOptimalGap)
        {
            FullTime = employee.GuaranteedHours,
            MaxWorkDays = AutofillSpecConstants.MaxWorkDays,
            MinRestDays = AutofillSpecConstants.MinRestDays,
            MaximumHours = 0,
            MinimumHours = 0,
            PerformsShiftWork = true,
            WorkOnMonday = true,
            WorkOnTuesday = true,
            WorkOnWednesday = true,
            WorkOnThursday = true,
            WorkOnFriday = true,
            WorkOnSaturday = true,
            WorkOnSunday = true,
            NightRate = AutofillSpecConstants.SurchargeRate,
            HolidayRate = AutofillSpecConstants.SurchargeRate,
            WE1Rate = AutofillSpecConstants.SurchargeRate,
            WE2Rate = AutofillSpecConstants.SurchargeRate,
            WE3Rate = AutofillSpecConstants.SurchargeRate,
        };
    }
}
