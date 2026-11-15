// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The pre-commit check of a plan written into a scenario counts the real duties of the people that the scenario does not
/// carry: a planned row that overlaps one is a collision, as it would be after an accept. A real duty on a shift the
/// scenario carries does not count a second time inside the cloned span.
/// </summary>

using Klacks.Api.Domain.Constants;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules;

[TestFixture]
public class PreCommitConflictCheckerScenarioOutsideTests
{
    private const int ScenarioMarginDays = 3;

    [Test]
    public async Task Check_Scenario_PlannedRowOverlappingARealDutyOutsideTheScenario_IsACollision()
    {
        var token = Guid.NewGuid();
        using var fixture = new PreCommitBatchParityFixture(token, PreCommitDisturbanceMode.None);
        var before = await CollisionsOfFullWeekAsync(fixture, token);
        fixture.Context.Work.Add(RealDutyOfFullWeek(Guid.NewGuid()));
        await fixture.Context.SaveChangesAsync();

        var after = await CollisionsOfFullWeekAsync(fixture, token);

        before.ShouldBe(0);
        after.ShouldBeGreaterThan(0, "the real duty stays in the plan after an accept");
    }

    [Test]
    public async Task Check_Scenario_RealDutyOnAShiftTheScenarioCarries_IsNoSecondDutyInsideTheClonedSpan()
    {
        var token = Guid.NewGuid();
        using var fixture = new PreCommitBatchParityFixture(token, PreCommitDisturbanceMode.None);
        var carriedShiftId = Guid.NewGuid();
        fixture.Context.Shift.Add(new Shift { Id = carriedShiftId, Name = "Carried", Status = ShiftStatus.SplitShift });
        fixture.Context.Shift.Add(new Shift
        {
            Id = Guid.NewGuid(),
            Name = "Clone",
            Status = ShiftStatus.SplitShift,
            AnalyseToken = token,
            ScenarioSourceShiftId = carriedShiftId,
        });
        fixture.Context.AnalyseScenarios.Add(new AnalyseScenario
        {
            Id = Guid.NewGuid(),
            Name = "Scenario",
            Token = token,
            FromDate = PreCommitBatchParityFixture.Sunday.AddDays(-ScenarioMarginDays),
            UntilDate = PreCommitBatchParityFixture.Sunday.AddDays(ScenarioMarginDays),
        });
        fixture.Context.Work.Add(RealDutyOfFullWeek(carriedShiftId));
        await fixture.Context.SaveChangesAsync();

        var collisions = await CollisionsOfFullWeekAsync(fixture, token);

        collisions.ShouldBe(0, "the clone stands for the real duty inside the cloned span");
    }

    private static Work RealDutyOfFullWeek(Guid shiftId) => new()
    {
        Id = Guid.NewGuid(),
        ClientId = PreCommitBatchParityFixture.FullWeek,
        ShiftId = shiftId,
        CurrentDate = PreCommitBatchParityFixture.Sunday,
        StartTime = new TimeOnly(10, 0),
        EndTime = new TimeOnly(14, 0),
        WorkTime = 4m,
    };

    private static async Task<int> CollisionsOfFullWeekAsync(PreCommitBatchParityFixture fixture, Guid token)
    {
        var result = await fixture.Checker.CheckAsync(fixture.PlannedRows, fixture.Removals, token);

        return result.NewConflicts.Count(finding =>
            finding.ClientId == PreCommitBatchParityFixture.FullWeek && finding.Comment == ScheduleValidationKeys.Collision);
    }
}
