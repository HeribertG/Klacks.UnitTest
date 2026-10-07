// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Application.Services.Schedules.Recovery;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Infrastructure.Services.Schedules;
using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.ScheduleOptimizer.Harmonizer.Conductor;
using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution.Initialization;
using Klacks.UnitTest.TestHelpers;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Services.Schedules;

/// <summary>
/// K14 (2026-10-07): several commands on one (agent, day) restrict cumulatively. Wizard 1 reads every command
/// (SlotConstraintFilter); Wizard 2 (HarmonizerKeywordDayResolver) and the recovery snapshot (ExtractKeywordDays)
/// hold one keyword per day and must reach the same verdict for every pair and triple of commands and every
/// shift kind.
/// </summary>
[TestFixture]
public class ScheduleCommandKeywordCombinerTests
{
    private const string AgentId = "00000000-0000-0000-0000-0000000000d1";
    private static readonly Guid Agent = Guid.Parse(AgentId);
    private static readonly DateOnly Day = new(2026, 4, 20);

    private static readonly ScheduleCommandKeyword[] AllKeywords = Enum.GetValues<ScheduleCommandKeyword>();

    private static readonly (int Index, CellSymbol Symbol, string Start, string End)[] ShiftKinds =
    [
        (ShiftTypeInference.EarlyIndex, CellSymbol.Early, "06:00", "14:00"),
        (ShiftTypeInference.LateIndex, CellSymbol.Late, "15:00", "22:00"),
        (ShiftTypeInference.NightIndex, CellSymbol.Night, "23:00", "05:00"),
    ];

    private static IEnumerable<ScheduleCommandKeyword[]> PairsAndTriples()
    {
        foreach (var a in AllKeywords)
        {
            foreach (var b in AllKeywords)
            {
                yield return [a, b];
                foreach (var c in AllKeywords)
                {
                    yield return [a, b, c];
                }
            }
        }
    }

    [TestCase(new[] { ScheduleCommandKeyword.NoEarly, ScheduleCommandKeyword.NoNight }, ScheduleCommandKeyword.OnlyLate)]
    [TestCase(new[] { ScheduleCommandKeyword.NotFree, ScheduleCommandKeyword.NoNight }, ScheduleCommandKeyword.NoNight)]
    [TestCase(new[] { ScheduleCommandKeyword.OnlyEarly, ScheduleCommandKeyword.OnlyLate }, ScheduleCommandKeyword.Free)]
    [TestCase(new[] { ScheduleCommandKeyword.OnlyEarly, ScheduleCommandKeyword.NoNight }, ScheduleCommandKeyword.OnlyEarly)]
    [TestCase(new[] { ScheduleCommandKeyword.OnlyEarly, ScheduleCommandKeyword.NoEarly }, ScheduleCommandKeyword.Free)]
    [TestCase(new[] { ScheduleCommandKeyword.NoEarly, ScheduleCommandKeyword.NoLate, ScheduleCommandKeyword.NoNight }, ScheduleCommandKeyword.Free)]
    [TestCase(new[] { ScheduleCommandKeyword.NotFree }, ScheduleCommandKeyword.NotFree)]
    [TestCase(new[] { ScheduleCommandKeyword.NotFree, ScheduleCommandKeyword.Free }, ScheduleCommandKeyword.Free)]
    [TestCase(new[] { ScheduleCommandKeyword.NoLate }, ScheduleCommandKeyword.NoLate)]
    public void Combine_ReducesTheAllowedShiftKinds(ScheduleCommandKeyword[] keywords, ScheduleCommandKeyword expected)
    {
        ScheduleCommandKeywordCombiner.Combine(keywords).ShouldBe(expected);
        ScheduleCommandKeywordCombiner.Combine(keywords.Reverse()).ShouldBe(expected, "the order of the commands must not matter");
    }

    [Test]
    public void Combine_WithoutCommands_IsNull()
    {
        ScheduleCommandKeywordCombiner.Combine([]).ShouldBeNull();
    }

    [Test]
    public void Wizard1Wizard2AndRecovery_AgreeForEveryPairAndTriple()
    {
        var mismatches = new List<string>();
        var keywordMap = ScheduleCommandKeywordMapper.BuildMap(ScheduleCommandKeywordTestFactory.Default);
        var tokenOf = keywordMap.GroupBy(e => e.Value).ToDictionary(g => g.Key, g => g.First().Key);

        foreach (var combination in PairsAndTriples())
        {
            var combined = ScheduleCommandKeywordCombiner.Combine(combination)!.Value;

            var recovery = RecoverySnapshotBuilder.ExtractKeywordDays(
                combination.Select(k => new ScheduleCommand { ClientId = Agent, CurrentDate = Day, CommandKeyword = tokenOf[k] }).ToList(),
                keywordMap)[(Agent, Day)];
            if (recovery != combined)
            {
                mismatches.Add($"{string.Join("+", combination)}: recovery {recovery}, combined {combined}");
            }

            var harmonizer = HarmonizerAvailability(combination);
            foreach (var kind in ShiftKinds)
            {
                var wizard1 = Wizard1Allows(combination, kind.Index, kind.Start, kind.End);
                var wizard2 = harmonizer.DiagnoseDayRestrictions(BitmapAgentFor(), new Cell(kind.Symbol, null, [], false), Day, "row") is null;
                if (wizard1 != wizard2)
                {
                    mismatches.Add($"{string.Join("+", combination)} {kind.Symbol}: wizard1 {wizard1}, wizard2 {wizard2}");
                }
            }
        }

        mismatches.ShouldBeEmpty();
    }

    private static BitmapAgent BitmapAgentFor() => new(AgentId, AgentId, 0m, new HashSet<CellSymbol>());

    private static DomainAwareReplaceValidator HarmonizerAvailability(IEnumerable<ScheduleCommandKeyword> combination)
    {
        var days = HarmonizerKeywordDayResolver.Resolve(combination.Select(k => (Agent, Day, k)));
        days.Restrictions.TryGetValue((Agent, Day), out var restriction);
        var availability = new DayAvailability(
            WorksOnDay: true,
            HasFreeCommand: days.FreeDates.Contains((Agent, Day)),
            HasBreakBlocker: false,
            RequiredSymbol: restriction.Required,
            ForbiddenSymbol: restriction.Forbidden);
        return new DomainAwareReplaceValidator(new Dictionary<(string, DateOnly), DayAvailability> { [(AgentId, Day)] = availability });
    }

    private static bool Wizard1Allows(IEnumerable<ScheduleCommandKeyword> combination, int shiftTypeIndex, string start, string end)
    {
        var agent = new CoreAgent(AgentId, 0, 0, 6, 0, 0.5, 0, 0, 2) { WorkOnMonday = true };
        var context = new CoreWizardContext
        {
            PeriodFrom = Day,
            PeriodUntil = Day,
            Agents = [agent],
            ScheduleCommands = combination.Select(k => new CoreScheduleCommand(AgentId, Day, k)).ToList(),
        };
        var startAt = Day.ToDateTime(TimeOnly.Parse(start));
        var endAt = Day.ToDateTime(TimeOnly.Parse(end));
        if (endAt <= startAt)
        {
            endAt = endAt.AddDays(1);
        }

        return !SlotConstraintFilter.ViolatesAbsoluteVeto(agent, Day, shiftTypeIndex, Guid.Empty, context, [], startAt, endAt);
    }
}
