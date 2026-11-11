// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;

namespace Klacks.UnitTest.TestHelpers;

/// <summary>
/// Builds fixed in-memory scenarios for the Holistic Harmonizer component tests. Every scenario is a fragmented or
/// unbalanced plan constructed from arithmetic day patterns (no randomness in the day layout, no database).
/// </summary>
public static class HarmonizerEvalScenarioFactory
{
    private const string FragmentedSmallName = "fragmented-4x14";
    private const string FragmentedMediumName = "fragmented-6x21";
    private const string UnbalancedLoadName = "unbalanced-5x14";

    private const decimal ShiftHours = 8m;
    private const decimal TargetLoadFactor = 0.6m;
    private const decimal MaxWeeklyHours = 40m;
    private const int MaxConsecutiveDays = 6;
    private const decimal MinPauseHours = 11m;

    private const int EveryOtherDayModulus = 2;
    private const int TwoOfThreeDaysModulus = 3;
    private const int TwoOfThreeDaysFreeRemainder = 2;
    private const int OverworkedAgentCount = 2;
    private const int OverworkedFreeDayModulus = 4;
    private const int UnderworkedWorkDayModulus = 4;

    private static readonly DateOnly PeriodStart = new(2026, 1, 5);

    private static readonly TimeOnly[] ShiftStartTimes =
    {
        new(6, 0),
        new(14, 0),
        new(22, 0),
    };

    private static readonly CellSymbol[] ShiftSymbols =
    {
        CellSymbol.Early,
        CellSymbol.Late,
        CellSymbol.Night,
    };

    public static IReadOnlyList<HarmonizerEvalScenario> CreateAll() =>
    [
        FragmentedSmall(),
        FragmentedMedium(),
        UnbalancedLoad(),
    ];

    private static HarmonizerEvalScenario FragmentedSmall() =>
        Build(FragmentedSmallName, agents: 4, days: 14,
            worksOn: (agent, day) => (agent + day) % EveryOtherDayModulus == 0,
            shiftIndex: (agent, day) => (agent + day) % ShiftSymbols.Length);

    private static HarmonizerEvalScenario FragmentedMedium() =>
        Build(FragmentedMediumName, agents: 6, days: 21,
            worksOn: (agent, day) => (agent + day) % TwoOfThreeDaysModulus != TwoOfThreeDaysFreeRemainder,
            shiftIndex: (agent, day) => (agent * 2 + day) % ShiftSymbols.Length);

    private static HarmonizerEvalScenario UnbalancedLoad() =>
        Build(UnbalancedLoadName, agents: 5, days: 14,
            worksOn: (agent, day) => agent < OverworkedAgentCount
                ? day % OverworkedFreeDayModulus != OverworkedFreeDayModulus - 1
                : (agent + day) % UnderworkedWorkDayModulus == 0,
            shiftIndex: (agent, day) => (agent + day) % ShiftSymbols.Length);

    private static HarmonizerEvalScenario Build(
        string name,
        int agents,
        int days,
        Func<int, int, bool> worksOn,
        Func<int, int, int> shiftIndex)
    {
        var agentList = new List<BitmapAgent>(agents);
        var agentIds = new List<string>(agents);
        for (var a = 0; a < agents; a++)
        {
            var id = Guid.NewGuid().ToString();
            agentIds.Add(id);
            agentList.Add(new BitmapAgent(
                Id: id,
                DisplayName: $"Eval Agent {a + 1:00}",
                TargetHours: days * ShiftHours * TargetLoadFactor,
                PreferredShiftSymbols: new HashSet<CellSymbol>(),
                MaxWeeklyHours: MaxWeeklyHours,
                MaxConsecutiveDays: MaxConsecutiveDays,
                MinPauseHours: MinPauseHours));
        }

        var shiftIds = new Guid[ShiftSymbols.Length];
        for (var s = 0; s < shiftIds.Length; s++)
        {
            shiftIds[s] = Guid.NewGuid();
        }

        var assignments = new List<BitmapAssignment>();
        for (var d = 0; d < days; d++)
        {
            var date = PeriodStart.AddDays(d);
            for (var a = 0; a < agents; a++)
            {
                if (!worksOn(a, d))
                {
                    continue;
                }

                var s = shiftIndex(a, d);
                var startAt = date.ToDateTime(ShiftStartTimes[s]);
                assignments.Add(new BitmapAssignment(
                    AgentId: agentIds[a],
                    Date: date,
                    Symbol: ShiftSymbols[s],
                    ShiftRefId: shiftIds[s],
                    WorkIds: [Guid.NewGuid()],
                    IsLocked: false,
                    StartAt: startAt,
                    EndAt: startAt.AddHours((double)ShiftHours),
                    Hours: ShiftHours));
            }
        }

        var input = new BitmapInput(
            Agents: agentList,
            StartDate: PeriodStart,
            EndDate: PeriodStart.AddDays(days - 1),
            Assignments: assignments);

        return new HarmonizerEvalScenario(name, input);
    }
}
