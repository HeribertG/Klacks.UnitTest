// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.Services.Schedules.HolisticHarmonizer;
using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Services.Schedules.HolisticHarmonizer;

/// <summary>
/// The LLM mode of stage 3 tells the model each employee's shift-work flag, so it does not propose late or night
/// duties for an employee who only works early duties (the validator rejects them anyway).
/// </summary>
[TestFixture]
public class HolisticHarmonizerEngineAgentSummaryTests
{
    private static readonly DateOnly Day0 = new(2026, 1, 5);

    [Test]
    public void BuildAgentSummary_StatesTheShiftWorkFlagPerEmployee()
    {
        var agents = new List<BitmapAgent>
        {
            new("agent-0", "Shift Worker", 40m, new HashSet<CellSymbol>()),
            new("agent-1", "Early Only", 40m, new HashSet<CellSymbol>(), PerformsShiftWork: false),
        };
        var bitmap = BitmapBuilder.Build(new BitmapInput(agents, Day0, Day0, []));

        var lines = HolisticHarmonizerEngine.BuildAgentSummary(bitmap)
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        lines.Length.ShouldBe(agents.Count);
        lines.Single(line => line.Contains("Shift Worker", StringComparison.Ordinal)).ShouldContain(" shiftWork=yes ");
        lines.Single(line => line.Contains("Early Only", StringComparison.Ordinal)).ShouldContain(" shiftWork=no ");
    }
}
