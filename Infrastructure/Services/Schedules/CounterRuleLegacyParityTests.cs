// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Byte-identity gate for the CounterRuleEvaluator adapter: a seeded corpus of generated scenarios (several
/// rules, several calendar periods, unsorted and duplicate planned dates, cross-midnight and 24-hour segments,
/// night-window overrides, industry scoping, per-rule and global enforcement, scenario tokens) runs through the
/// frozen pre-adapter evaluator and through the production adapter; the serialized notification lists must be
/// equal, entry order and CommentParams order included.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.DTOs.Notifications;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Scheduling;
using Klacks.Api.Domain.Models.Scheduling;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.Schedules;
using Microsoft.EntityFrameworkCore;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules;

[TestFixture]
public class CounterRuleLegacyParityTests
{
    private const int CorpusSize = 400;
    private const int MaxRules = 3;
    private const int MaxWorks = 30;
    private const int MaxPlannedSlots = 7;
    private const int MaxSegmentsPerDay = 3;
    private const int HalfHourSteps = 48;
    private const int MinimumScenariosPerCoverageClass = 20;

    private static readonly DateOnly CorpusStart = new(2025, 11, 1);
    private static readonly int CorpusDays = 500;

    private static readonly (string? Start, string? End)[] NightWindows =
    [
        (null, null),
        ("20:00", "05:00"),
        ("22:00", "06:00"),
        ("00:00", "04:00"),
    ];

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    [Test]
    public async Task GeneratedCorpus_AdapterMatchesLegacyEvaluatorByteForByte()
    {
        var mismatches = new List<string>();
        var coverage = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var seed = 0; seed < CorpusSize; seed++)
        {
            var mismatch = await RunScenarioAsync(seed, coverage);
            if (mismatch is not null)
            {
                mismatches.Add(mismatch);
            }
        }

        mismatches.ShouldBeEmpty(string.Join(Environment.NewLine, mismatches.Take(5)));
        TestContext.Out.WriteLine(string.Join(", ", coverage.OrderBy(c => c.Key).Select(c => $"{c.Key}={c.Value}")));
        foreach (var coverageClass in CoverageClasses)
        {
            coverage.GetValueOrDefault(coverageClass).ShouldBeGreaterThanOrEqualTo(
                MinimumScenariosPerCoverageClass, $"The corpus no longer exercises '{coverageClass}' - the byte comparison would prove little.");
        }
    }

    private static readonly string[] CoverageClasses =
    [
        "asOfReported",
        "plannedReported",
        "plannedSeveralEntries",
        "plannedSeveralDates",
        "errorEntry",
        "scopedRuleReported",
        "scenarioTokenReported",
    ];

    private static void Count(Dictionary<string, int> coverage, string coverageClass, bool condition)
    {
        if (condition)
        {
            coverage[coverageClass] = coverage.GetValueOrDefault(coverageClass) + 1;
        }
    }

    private static async Task<string?> RunScenarioAsync(int seed, Dictionary<string, int> coverage)
    {
        var random = new Random(seed);
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase($"counter-rule-parity-{seed}-{Guid.NewGuid()}")
            .Options;
        await using var context = new DataBaseContext(options, null!);

        var clientId = Guid.NewGuid();
        var scenarioToken = Guid.NewGuid();
        var analyseToken = random.Next(4) == 0 ? scenarioToken : (Guid?)null;
        var contractRuleId = Guid.NewGuid();
        var rules = BuildRules(random, contractRuleId);
        var globalMode = random.Next(2) == 0 ? RuleEnforcementMode.Warn : RuleEnforcementMode.Block;
        var window = NightWindows[random.Next(NightWindows.Length)];
        var contractData = new EffectiveContractData
        {
            NightStart = window.Start,
            NightEnd = window.End,
            SchedulingRuleId = random.Next(2) == 0 ? contractRuleId : Guid.NewGuid(),
        };

        SeedWorks(context, random, clientId, scenarioToken);

        var ruleRepository = Substitute.For<ICounterRuleRepository>();
        ruleRepository.GetAllActiveAsync().Returns(_ => rules.Select(Clone).ToList());
        var enforcementResolver = Substitute.For<IComplianceEnforcementResolver>();
        enforcementResolver.GetModeAsync(ComplianceRuleNames.CounterRule).Returns(globalMode);
        var contractDataProvider = Substitute.For<IClientContractDataProvider>();
        contractDataProvider
            .GetEffectiveContractDataAsync(Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<int?>())
            .Returns(contractData);

        var legacy = new LegacyCounterRuleEvaluatorOracle(ruleRepository, context, enforcementResolver, contractDataProvider);
        var adapter = new CounterRuleEvaluator(ruleRepository, context, enforcementResolver, contractDataProvider);

        var asOf = RandomDate(random);
        var expectedAsOf = await legacy.EvaluateAsync(clientId, "Anna", asOf, analyseToken);
        var actualAsOf = await adapter.EvaluateAsync(clientId, "Anna", asOf, analyseToken);
        var asOfMismatch = Compare(seed, "EvaluateAsync", expectedAsOf, actualAsOf);
        if (asOfMismatch is not null)
        {
            return asOfMismatch;
        }

        var slots = BuildPlannedSlots(random);
        var expectedPlanned = await legacy.EvaluatePlannedAsync(clientId, "Anna", slots, analyseToken);
        var actualPlanned = await adapter.EvaluatePlannedAsync(clientId, "Anna", slots, analyseToken);

        var anyReported = expectedAsOf.Count > 0 || expectedPlanned.Count > 0;
        Count(coverage, "asOfReported", expectedAsOf.Count > 0);
        Count(coverage, "plannedReported", expectedPlanned.Count > 0);
        Count(coverage, "plannedSeveralEntries", expectedPlanned.Count > 1);
        Count(coverage, "plannedSeveralDates", expectedPlanned.Select(e => e.Date).Distinct().Count() > 1);
        Count(coverage, "errorEntry", expectedAsOf.Concat(expectedPlanned).Any(e => e.Type == ScheduleValidationType.Error));
        Count(coverage, "scopedRuleReported", anyReported && rules.Any(r => r.SchedulingRuleId.HasValue));
        Count(coverage, "scenarioTokenReported", anyReported && analyseToken.HasValue);
        return Compare(seed, "EvaluatePlannedAsync", expectedPlanned, actualPlanned);
    }

    private static string? Compare(
        int seed,
        string method,
        List<ScheduleValidationNotificationDto> expected,
        List<ScheduleValidationNotificationDto> actual)
    {
        var expectedJson = JsonSerializer.Serialize(expected, SerializerOptions);
        var actualJson = JsonSerializer.Serialize(actual, SerializerOptions);
        return string.Equals(expectedJson, actualJson, StringComparison.Ordinal)
            ? null
            : $"seed {seed} {method}:{Environment.NewLine}  legacy  {expectedJson}{Environment.NewLine}  adapter {actualJson}";
    }

    private static List<CounterRule> BuildRules(Random random, Guid contractRuleId)
    {
        var count = random.Next(MaxRules + 1);
        var rules = new List<CounterRule>(count);
        for (var i = 0; i < count; i++)
        {
            var eventType = (CounterEventType)(1 + random.Next(3));
            rules.Add(new CounterRule
            {
                Id = Guid.NewGuid(),
                EventType = eventType,
                Period = (CounterPeriod)(1 + random.Next(3)),
                Threshold = 1 + random.Next(6),
                HoursThreshold = eventType == CounterEventType.ShiftExceedingHours && random.Next(5) > 0
                    ? 4m + (random.Next(21) * 0.5m)
                    : null,
                SchedulingRuleId = random.Next(4) switch
                {
                    0 => contractRuleId,
                    1 => Guid.NewGuid(),
                    _ => null,
                },
                Enforcement = random.Next(3) switch
                {
                    0 => RuleEnforcementMode.Warn,
                    1 => RuleEnforcementMode.Block,
                    _ => null,
                },
            });
        }

        return rules;
    }

    private static CounterRule Clone(CounterRule rule) => new()
    {
        Id = rule.Id,
        EventType = rule.EventType,
        Period = rule.Period,
        Threshold = rule.Threshold,
        HoursThreshold = rule.HoursThreshold,
        SchedulingRuleId = rule.SchedulingRuleId,
        Enforcement = rule.Enforcement,
    };

    private static void SeedWorks(DataBaseContext context, Random random, Guid clientId, Guid scenarioToken)
    {
        var dayCount = random.Next(MaxWorks + 1);
        for (var i = 0; i < dayCount; i++)
        {
            var date = RandomDate(random);
            var segments = 1 + random.Next(MaxSegmentsPerDay);
            for (var s = 0; s < segments; s++)
            {
                var (start, end) = RandomSpan(random);
                context.Work.Add(new Klacks.Api.Domain.Models.Schedules.Work
                {
                    Id = Guid.NewGuid(),
                    ClientId = random.Next(10) == 0 ? Guid.NewGuid() : clientId,
                    ShiftId = Guid.NewGuid(),
                    CurrentDate = date,
                    StartTime = start,
                    EndTime = end,
                    WorkTime = 8m,
                    IsDeleted = random.Next(15) == 0,
                    AnalyseToken = random.Next(4) == 0 ? scenarioToken : null,
                });
            }
        }

        context.SaveChanges();
    }

    private static List<(DateOnly Date, TimeOnly StartTime, TimeOnly EndTime)> BuildPlannedSlots(Random random)
    {
        var count = random.Next(MaxPlannedSlots + 1);
        var slots = new List<(DateOnly, TimeOnly, TimeOnly)>(count);
        for (var i = 0; i < count; i++)
        {
            var date = slots.Count > 0 && random.Next(4) == 0 ? slots[random.Next(slots.Count)].Item1 : RandomDate(random);
            var (start, end) = RandomSpan(random);
            slots.Add((date, start, end));
        }

        return slots;
    }

    private static DateOnly RandomDate(Random random) => CorpusStart.AddDays(random.Next(CorpusDays));

    private static (TimeOnly Start, TimeOnly End) RandomSpan(Random random)
    {
        var start = TimeOnly.MinValue.AddMinutes(random.Next(HalfHourSteps) * 30);
        if (random.Next(12) == 0)
        {
            return (start, start);
        }

        var end = TimeOnly.MinValue.AddMinutes(random.Next(HalfHourSteps) * 30);
        return (start, end);
    }
}
