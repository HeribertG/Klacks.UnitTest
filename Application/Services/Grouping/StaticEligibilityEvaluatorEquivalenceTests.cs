// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Proves that the optimised StaticEligibilityEvaluator (per-client contract index, weekday masks,
/// O(1) verdict for clients without any active contract day) returns exactly the verdicts, reasons,
/// eligibility bits and weekday sets of the original per-day scan (NaiveEligibilityReference) on
/// seeded random inputs with contract changes in the middle of the period, clients without any
/// contract, shift work varying by day, shifts without run days, qualifications, blacklist entries
/// and hourly unavailability. Contract data covers only a window inside the period (as when contracts
/// start or end inside it), each client's contract starts and ends at random days inside that window, and
/// run days also fall outside the window, so the day-offset bounds of the contract index are exercised.
/// All ids come from the seeded random generator, so every case is reproducible.
/// </summary>

using Klacks.Api.Application.DTOs.Grouping;
using Klacks.Api.Application.Services.Grouping;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Staffs;

namespace Klacks.UnitTest.Application.Services.Grouping;

[TestFixture]
public class StaticEligibilityEvaluatorEquivalenceTests
{
    private const int PeriodDays = 28;
    private const int ClientCount = 24;
    private const int ShiftCount = 14;
    private const int MaxWindowMargin = 6;
    private const int GuidByteLength = 16;
    private static readonly DateOnly PeriodStart = new(2026, 6, 15);
    private static readonly Guid QualificationId = new("5b0c2f64-8a51-4c1e-9d3a-6f7e2b1a9c40");

    [TestCase(1)]
    [TestCase(7)]
    [TestCase(42)]
    [TestCase(1234)]
    [TestCase(98765)]
    [TestCase(2026)]
    [TestCase(31337)]
    public void OptimisedEvaluator_MatchesNaiveScan_OnRandomInputs(int seed)
    {
        var context = RandomContext(new Random(seed), out var clientIds, out var shiftIds, expiredBlocks: seed % 2 == 0);
        var firstContractDay = context.Contracts.Keys.Min();
        var lastContractDay = context.Contracts.Keys.Max();
        context.RunDays.Values.SelectMany(days => days)
            .ShouldContain(day => day < firstContractDay || day > lastContractDay, "run days outside the contract window");
        var optimised = new StaticEligibilityEvaluator(context);
        var naive = new NaiveEligibilityReference(context);

        foreach (var clientId in clientIds)
        {
            foreach (var shiftId in shiftIds)
            {
                var expected = naive.Evaluate(clientId, shiftId);
                optimised.IsEligible(clientId, shiftId).ShouldBe(expected.IsEligible, $"IsEligible {clientId} {shiftId}");
                optimised.Evaluate(clientId, shiftId).ShouldBe(expected, $"Evaluate {clientId} {shiftId}");
                optimised.EligibleWeekdays(clientId, shiftId)
                    .ShouldBe(naive.EligibleWeekdays(clientId, shiftId), ignoreOrder: true, $"Weekdays {clientId} {shiftId}");
            }
        }
    }

    [Test]
    public void ClientWithoutAnyContract_OnShiftWithoutRunDays_ReportsNoActiveContract()
    {
        var clientId = Guid.NewGuid();
        var shiftId = Guid.NewGuid();
        var context = new GroupingEligibilityContext(
            new Dictionary<Guid, GroupingShiftRecord> { [shiftId] = new(shiftId, "S", new TimeOnly(6, 0), new TimeOnly(14, 0), 1) },
            new Dictionary<Guid, IReadOnlyList<DateOnly>> { [shiftId] = [] },
            new Dictionary<DateOnly, IReadOnlyDictionary<Guid, EffectiveContractData>>(),
            new Dictionary<Guid, IReadOnlyList<ShiftRequiredQualification>>(),
            new Dictionary<Guid, IReadOnlyList<ClientQualification>>(),
            new HashSet<GroupingEntityPair>(),
            new Dictionary<(Guid ClientId, DateOnly Day), IReadOnlyList<ClientAvailability>>(),
            ExpiredMandatoryBlocks: false);

        var evaluator = new StaticEligibilityEvaluator(context);

        evaluator.Evaluate(clientId, shiftId).ShouldBe(EligibilityVerdict.Ineligible(GroupingIneligibilityReason.NoActiveContract));
        evaluator.IsEligible(clientId, shiftId).ShouldBeFalse();
        evaluator.EligibleWeekdays(clientId, shiftId).ShouldBeEmpty();
    }

    private static GroupingEligibilityContext RandomContext(
        Random random, out List<Guid> clientIdsOut, out List<Guid> shiftIdsOut, bool expiredBlocks)
    {
        var days = Enumerable.Range(0, PeriodDays).Select(PeriodStart.AddDays).ToList();
        var clientIds = Enumerable.Range(0, ClientCount).Select(_ => NextGuid(random)).ToList();
        var shiftIds = Enumerable.Range(0, ShiftCount).Select(_ => NextGuid(random)).ToList();
        clientIdsOut = clientIds;
        shiftIdsOut = shiftIds;

        var windowStart = 1 + random.Next(MaxWindowMargin);
        var windowEnd = PeriodDays - 2 - random.Next(MaxWindowMargin);
        var contracts = days
            .Where((_, index) => index >= windowStart && index <= windowEnd)
            .ToDictionary(day => day, _ => new Dictionary<Guid, EffectiveContractData>());
        foreach (var clientId in clientIds)
        {
            var kind = random.Next(5);
            if (kind == 0)
            {
                continue;
            }

            var switchDay = random.Next(PeriodDays);
            var contractStart = kind == 4 ? random.Next(windowStart, windowEnd + 1) : windowStart;
            var contractEnd = kind == 4 ? random.Next(contractStart, windowEnd + 1) : windowEnd;
            var first = RandomContract(random, active: kind != 3 || random.Next(2) == 0);
            var second = RandomContract(random, active: random.Next(5) != 0);
            foreach (var (day, index) in days.Select((day, index) => (day, index)))
            {
                if (index < contractStart || index > contractEnd || (kind == 2 && index < switchDay / 2))
                {
                    continue;
                }

                contracts[day][clientId] = index < switchDay ? first : second;
            }
        }

        var shifts = new Dictionary<Guid, GroupingShiftRecord>();
        var runDays = new Dictionary<Guid, IReadOnlyList<DateOnly>>();
        var requirements = new Dictionary<Guid, IReadOnlyList<ShiftRequiredQualification>>();
        foreach (var shiftId in shiftIds)
        {
            var start = new TimeOnly(random.Next(24), random.Next(2) * 30);
            var end = start.AddHours(random.Next(4, 10));
            shifts[shiftId] = new GroupingShiftRecord(shiftId, "S", start, end, 1);
            var weekdays = Enum.GetValues<DayOfWeek>().Where(_ => random.Next(3) != 0).ToHashSet();
            runDays[shiftId] = random.Next(8) == 0
                ? []
                : days.Where(day => weekdays.Contains(day.DayOfWeek) && random.Next(6) != 0).ToList();
            requirements[shiftId] = random.Next(3) == 0
                ?
                [
                    new ShiftRequiredQualification
                    {
                        ShiftId = shiftId, QualificationId = QualificationId, IsMandatory = true, MinLevel = QualificationLevel.Proficient,
                    },
                ]
                : [];
        }

        var qualifications = clientIds
            .Where(_ => random.Next(2) == 0)
            .ToDictionary(
                clientId => clientId,
                clientId => (IReadOnlyList<ClientQualification>)
                [
                    new ClientQualification
                    {
                        ClientId = clientId,
                        QualificationId = QualificationId,
                        Level = random.Next(2) == 0 ? QualificationLevel.Expert : QualificationLevel.Basic,
                        ValidUntil = random.Next(3) == 0 ? PeriodStart.AddDays(random.Next(PeriodDays)) : null,
                    },
                ]);

        var blacklist = clientIds
            .SelectMany(clientId => shiftIds.Where(_ => random.Next(10) == 0).Select(shiftId => new GroupingEntityPair(clientId, shiftId)))
            .ToHashSet();

        var availability = clientIds
            .SelectMany(clientId => days
                .Where(_ => random.Next(6) == 0)
                .SelectMany(day => Enumerable.Range(0, 24).Select(hour => new ClientAvailability
                {
                    ClientId = clientId, Date = day, Hour = hour, IsAvailable = false,
                })))
            .GroupBy(entry => (entry.ClientId, entry.Date))
            .ToDictionary(group => (group.Key.ClientId, group.Key.Date), group => (IReadOnlyList<ClientAvailability>)group.ToList());

        return new GroupingEligibilityContext(
            shifts,
            runDays,
            contracts.ToDictionary(entry => entry.Key, entry => (IReadOnlyDictionary<Guid, EffectiveContractData>)entry.Value),
            requirements,
            qualifications,
            blacklist,
            availability,
            expiredBlocks);
    }

    private static Guid NextGuid(Random random)
    {
        var bytes = new byte[GuidByteLength];
        random.NextBytes(bytes);
        return new Guid(bytes);
    }

    private static EffectiveContractData RandomContract(Random random, bool active) => new()
    {
        HasActiveContract = active,
        PerformsShiftWork = random.Next(2) == 0,
        WorkOnMonday = random.Next(3) != 0,
        WorkOnTuesday = random.Next(3) != 0,
        WorkOnWednesday = random.Next(3) != 0,
        WorkOnThursday = random.Next(3) != 0,
        WorkOnFriday = random.Next(3) != 0,
        WorkOnSaturday = random.Next(3) != 0,
        WorkOnSunday = random.Next(3) != 0,
    };
}
