// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Parity of the pre-commit conflict checker between one check of several people and the checks of each person alone,
/// on the real evaluators over an in-memory database (PreCommitBatchParityFixture): the same findings with the same
/// severity, counted, and per person in the same order. Runs on the real plan and inside a scenario. A second test
/// pins that the seeded plan makes every rule family report, so the comparison is not one of two empty lists. A third
/// pins the rows that must not count (works, breaks, work changes of the other world, soft-deleted rows, a container
/// child): the check of the fixture with them reports exactly what the check of the fixture without them reports, and
/// the same rows seeded as ordinary rows of the plan add a finding to their person each, so no row is a silent no-op.
/// </summary>

using Klacks.Api.Application.DTOs.Notifications;
using Klacks.Api.Domain.Constants;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules;

[TestFixture]
public class PreCommitConflictCheckerBatchParityTests
{
    private const string BlockTypeCorrection = "Correction";
    private const string BlockTypeBreak = "Break";

    private static readonly string[] CommentsOfEveryRuleFamily =
    [
        ScheduleValidationKeys.Collision,
        ScheduleValidationKeys.RestViolation,
        ScheduleValidationKeys.Overtime,
        ScheduleValidationKeys.WeeklyOvertime,
        ScheduleValidationKeys.ConsecutiveDays,
        QualificationValidationKeys.Expired,
        ScheduleValidationKeys.PeriodCap,
        ScheduleValidationKeys.PeriodCapApproaching,
        ScheduleValidationKeys.PeriodCapOvertime,
        ScheduleValidationKeys.RollingAverage,
        ScheduleValidationKeys.RestDayRotation,
        ScheduleValidationKeys.CounterRule,
        ScheduleValidationKeys.RestrictedTimeWindow,
        ScheduleValidationKeys.CompensatoryRestDue,
        ScheduleValidationKeys.CompensatoryRestOverdue,
        ScheduleValidationKeys.HolidayWork,
        ScheduleValidationKeys.DayDirective,
    ];

    [TestCase(false)]
    [TestCase(true)]
    public async Task CheckOfAllPeopleInOneCall_ReportsWhatTheChecksOfEachPersonAloneReport(bool inScenario)
    {
        // Arrange
        Guid? analyseToken = inScenario ? Guid.NewGuid() : null;
        using var fixture = new PreCommitBatchParityFixture(analyseToken);

        // Act
        var together = (await fixture.Checker.CheckAsync(fixture.PlannedRows, fixture.Removals, analyseToken)).NewConflicts;
        var alone = new List<ScheduleValidationNotificationDto>();
        foreach (var clientId in fixture.AffectedClients)
        {
            var result = await fixture.Checker.CheckAsync(
                fixture.PlannedRows.Where(row => row.ClientId == clientId).ToList(),
                fixture.Removals.Where(removal => removal.ClientId == clientId).ToList(),
                analyseToken);
            alone.AddRange(result.NewConflicts);
        }

        // Assert
        together.ShouldNotBeEmpty();
        Sorted(together).ShouldBe(Sorted(alone));
        foreach (var clientId in fixture.AffectedClients)
        {
            together.Where(finding => finding.ClientId == clientId).Select(PreCommitBatchParityFixture.Identity)
                .ShouldBe(
                    alone.Where(finding => finding.ClientId == clientId).Select(PreCommitBatchParityFixture.Identity),
                    "the findings of one person must keep their order");
        }
    }

    [Test]
    public async Task TheSeededPlan_MakesEveryRuleFamilyReport()
    {
        // Arrange
        using var fixture = new PreCommitBatchParityFixture();

        // Act
        var findings = (await fixture.Checker.CheckAsync(fixture.PlannedRows, fixture.Removals)).NewConflicts;

        // Assert
        var comments = findings.Select(finding => finding.Comment).ToHashSet();
        foreach (var comment in CommentsOfEveryRuleFamily)
        {
            comments.ShouldContain(comment);
        }

        findings.ShouldContain(finding =>
            finding.ClientId == PreCommitBatchParityFixture.Unqualified && finding.Type == ScheduleValidationType.Error,
            "the missing mandatory qualification");
        CollisionOf(findings, PreCommitBatchParityFixture.Collider).CommentParams.Values
            .ShouldContain(BlockTypeCorrection, "Collider collides with the correction of the own work only");
        CollisionOf(findings, PreCommitBatchParityFixture.Lenient).CommentParams.Values
            .ShouldContain(BlockTypeCorrection, "Lenient collides with the correction of the own work only");
        findings.ShouldContain(finding =>
            finding.ClientId == PreCommitBatchParityFixture.Swapper && finding.Comment == ScheduleValidationKeys.RestDayRotation,
            "Swapper worked the Sunday before the plan");
        CollisionOf(findings, PreCommitBatchParityFixture.Outdoor).CommentParams.Values
            .ShouldContain(BlockTypeBreak, "Outdoor collides with the own absence only");
        findings.ShouldNotContain(finding =>
            finding.ClientId == PreCommitBatchParityFixture.ShortRest && finding.Comment == ScheduleValidationKeys.RestDayRotation,
            "the window starts before ShortRest's employment start");
        findings.ShouldNotContain(finding =>
            finding.ClientId == PreCommitBatchParityFixture.Swapper && finding.Comment == ScheduleValidationKeys.Collision,
            "the swap vacates the work the planned row replaces");
        findings.Select(finding => finding.ClientId).Distinct().Count().ShouldBeGreaterThan(5);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task RowsThatMustNotCount_AreIgnored_AndEachOneWouldAddAFindingIfItCounted(bool inScenario)
    {
        // Arrange
        Guid? analyseToken = inScenario ? Guid.NewGuid() : null;
        using var without = new PreCommitBatchParityFixture(analyseToken, PreCommitDisturbanceMode.None);
        using var ignored = new PreCommitBatchParityFixture(analyseToken, PreCommitDisturbanceMode.Ignorable);
        using var counted = new PreCommitBatchParityFixture(analyseToken, PreCommitDisturbanceMode.Live);

        // Act
        var findingsWithout = await CheckAllAsync(without, analyseToken);
        var findingsIgnored = await CheckAllAsync(ignored, analyseToken);
        var findingsCounted = await CheckAllAsync(counted, analyseToken);

        // Assert
        findingsWithout.ShouldNotBeEmpty();
        Sorted(findingsIgnored).ShouldBe(Sorted(findingsWithout), "rows of the other world, soft-deleted rows and a container child must change nothing");
        foreach (var disturbance in PreCommitBatchParityFixture.Disturbances.Where(entry => !inScenario || entry.VisibleInScenario))
        {
            CountOf(findingsCounted, disturbance.ClientId, disturbance.Comment)
                .ShouldBeGreaterThan(
                    CountOf(findingsWithout, disturbance.ClientId, disturbance.Comment),
                    $"the row '{disturbance.Name}' must add a {disturbance.Comment} finding when it counts, or the test proves nothing about it");
            CountOf(findingsIgnored, disturbance.ClientId, disturbance.Comment)
                .ShouldBe(
                    CountOf(findingsWithout, disturbance.ClientId, disturbance.Comment),
                    $"the row '{disturbance.Name}' was counted");
        }
    }

    private static async Task<List<ScheduleValidationNotificationDto>> CheckAllAsync(PreCommitBatchParityFixture fixture, Guid? analyseToken)
        => (await fixture.Checker.CheckAsync(fixture.PlannedRows, fixture.Removals, analyseToken)).NewConflicts.ToList();

    private static int CountOf(IEnumerable<ScheduleValidationNotificationDto> findings, Guid clientId, string comment)
        => findings.Count(finding => finding.ClientId == clientId && finding.Comment == comment);

    private static ScheduleValidationNotificationDto CollisionOf(
        IEnumerable<ScheduleValidationNotificationDto> findings, Guid clientId)
        => findings.Single(finding => finding.ClientId == clientId && finding.Comment == ScheduleValidationKeys.Collision);

    private static List<string> Sorted(IEnumerable<ScheduleValidationNotificationDto> findings)
        => findings.Select(PreCommitBatchParityFixture.Identity).OrderBy(identity => identity, StringComparer.Ordinal).ToList();
}
