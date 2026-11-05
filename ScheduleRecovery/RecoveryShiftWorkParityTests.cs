// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.Services.Schedules.Recovery;
using Klacks.ScheduleOptimizer.TokenEvolution.Initialization;
using Klacks.ScheduleRecovery.Engine;
using Klacks.ScheduleRecovery.Model;
using NUnit.Framework;
using Shouldly;
using static Klacks.UnitTest.ScheduleRecovery.RecoveryTestKit;

namespace Klacks.UnitTest.ScheduleRecovery;

/// <summary>
/// Parity of the shift-work rule between the Recovery engine (its own check, it cannot reference the optimizer) and
/// <see cref="ShiftWorkRule"/>, which the wizards use: over a grid of shift spans, an employee without shift work is
/// offered as cover exactly when the shared rule permits the shift. The span is classified as the recovery snapshot
/// does it (<see cref="ShiftCategoryClassifier"/>).
/// </summary>
[TestFixture]
public sealed class RecoveryShiftWorkParityTests
{
    private const int ShiftHours = 8;
    private const int HoursPerDay = 24;

    private static readonly int[] StartHours = [0, 4, 5, 6, 7, 8, 10, 12, 14, 15, 16, 18, 20, 22, 23];

    private readonly IRecoveryEngine _engine = new LocalRepairEngine();

    [Test]
    public void EarlyOnlyCandidate_IsOfferedExactlyWhenTheSharedRulePermits([ValueSource(nameof(StartHours))] int startHour)
    {
        var d = Day(6, 3);
        var next = d.AddDays(1);
        var s = Shift(1);
        var start = TimeOnly.FromTimeSpan(TimeSpan.FromHours(startHour));
        var end = TimeOnly.FromTimeSpan(TimeSpan.FromHours((startHour + ShiftHours) % HoursPerDay));
        var startAt = At(d, startHour);
        var endAt = startAt.AddHours(ShiftHours);
        var category = ShiftCategoryClassifier.CategoryOf(start.ToTimeSpan(), end.ToTimeSpan());

        var snapshot = new SnapshotBuilder()
            .Days(d, next)
            .Agent(Agent(1), "absent")
            .Agent(Agent(2), "early-only candidate", performsShiftWork: false)
            .Work(Agent(1), d, s, category, startAt, endAt, ShiftHours)
            .Build();

        var proposal = _engine.Repair(snapshot, new AbsenceEvent(Agent(1), [d]), Ruleset.Default);

        var covered = proposal.Deltas.Any(delta => delta.ToAgentId == Agent(2));
        var permitted = ShiftWorkRule.Permits(false, ShiftTypeInference.FromSpan(start, end));
        covered.ShouldBe(permitted, $"{start}-{end} ({category})");
    }
}
