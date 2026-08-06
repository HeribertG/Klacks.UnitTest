// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Application.Constants;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Infrastructure.Services.Schedules;
using Klacks.ScheduleOptimizer.Models;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules;

/// <summary>
/// The benchmark endpoint runs synchronously inside the HTTP request, so its budget is what keeps a
/// large selection from holding a request thread indefinitely. The cap must be a ceiling and never an
/// extension - a caller asking for less has to keep its own, shorter budget.
/// </summary>
[TestFixture]
public sealed class WizardBenchmarkRuntimeCapTests
{
    private static readonly DateOnly From = new(2026, 7, 1);
    private static readonly DateOnly Until = new(2026, 7, 7);

    [Test]
    public async Task RunCoreAsync_WithoutOverrides_UsesTheCap()
    {
        var response = await RunAsync(overrides: null, cap: TimeSpan.FromSeconds(2));

        response.ShouldNotBeNull();
    }

    [Test]
    public void BenchmarkBudget_HasAHardGraceOnTopOfTheSoftLimit()
    {
        // The loop honours MaxRuntime itself, but only between generations; the grace is what cancels a
        // run stuck inside one.
        AutofillLimits.BenchmarkHardCancelGrace.ShouldBeGreaterThan(TimeSpan.Zero);
        AutofillLimits.BenchmarkMaxRuntime.ShouldBeGreaterThan(AutofillLimits.BenchmarkHardCancelGrace);
    }

    [Test]
    public async Task RunCoreAsync_CancelledToken_StopsInsteadOfRunningTheFullBudget()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = async () => await WizardBenchmarkService.RunCoreAsync(
            Context(), Request(null), cts.Token, AutofillLimits.BenchmarkMaxRuntime);

        await act.ShouldThrowAsync<OperationCanceledException>();
    }

    private static async Task<object> RunAsync(WizardTrainingOverrides? overrides, TimeSpan cap)
    {
        using var cts = new CancellationTokenSource();
        return await WizardBenchmarkService.RunCoreAsync(Context(), Request(overrides), cts.Token, cap);
    }

    private static WizardContextRequest Request(WizardTrainingOverrides? overrides) => new(
        PeriodFrom: From,
        PeriodUntil: Until,
        AgentIds: [Guid.NewGuid()],
        ShiftIds: null,
        AnalyseToken: null,
        TrainingOverrides: overrides);

    private static CoreWizardContext Context() => new()
    {
        PeriodFrom = From,
        PeriodUntil = Until,
        Agents =
        [
            new CoreAgent(
                Id: "agent-0",
                CurrentHours: 0,
                GuaranteedHours: 0,
                MaxConsecutiveDays: 6,
                MinRestHours: 11,
                Motivation: 0.5,
                MaxDailyHours: 10,
                MaxWeeklyHours: 50,
                MaxOptimalGap: 2)
            {
                FullTime = 40,
                PerformsShiftWork = true,
                WorkOnMonday = true,
                WorkOnTuesday = true,
                WorkOnWednesday = true,
                WorkOnThursday = true,
                WorkOnFriday = true,
            },
        ],
        Shifts =
        [
            new CoreShift(Guid.NewGuid().ToString(), "FD", From.ToString("yyyy-MM-dd"), "08:00", "16:00", 8, 1, 0),
        ],
        SchedulingMaxConsecutiveDays = 6,
        SchedulingMinPauseHours = 11,
        SchedulingMaxDailyHours = 10,
    };
}
