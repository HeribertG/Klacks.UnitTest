// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Parity of each rule evaluator behind the pre-commit check between its evaluation of several people in one call and
/// its single-person evaluation called once per person, on the real implementations over an in-memory database
/// (PreCommitBatchParityFixture): the same findings with the same severity in the same order. Runs on the real plan and
/// inside a scenario; where the scenario reports nothing by design (compensatory rest), the test says so.
/// </summary>

using Klacks.Api.Application.DTOs.Notifications;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Domain.Services.Schedules;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules;

[TestFixture]
public class PreCommitRuleEvaluatorBatchParityTests
{
    private const decimal ShiftHours = 8m;
    private const int YearOfThePlan = 2026;

    [TestCase(false)]
    [TestCase(true)]
    public async Task PeriodCap_ForSeveralPeople_ReportsWhatEachPersonAloneReports(bool inScenario)
    {
        // Arrange
        Guid? analyseToken = inScenario ? Guid.NewGuid() : null;
        using var fixture = new PreCommitBatchParityFixture(analyseToken);
        var clients = fixture.PlannedRows
            .GroupBy(row => row.ClientId)
            .Select(group => new ClientPlannedHours(
                group.Key, group.Select(row => (row.Date, Hours: ShiftHours + row.ExtraPaidHours)).ToList()))
            .Append(new ClientPlannedHours(
                PreCommitBatchParityFixture.GiverOnly, [(PreCommitBatchParityFixture.Tuesday, -ShiftHours)]))
            .ToList();

        // Act
        var together = await fixture.PeriodCap.EvaluatePlannedForClientsAsync(clients, analyseToken);
        var alone = new List<ScheduleValidationNotificationDto>();
        foreach (var client in clients)
        {
            alone.AddRange(await fixture.PeriodCap.EvaluatePlannedAsync(
                client.ClientId, string.Empty, client.PlannedHours, analyseToken));
        }

        // Assert
        together.ShouldNotBeEmpty();
        Identities(together).ShouldBe(Identities(alone));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task RestDayRotation_ForSeveralPeople_ReportsWhatEachPersonAloneReports(bool inScenario)
    {
        // Arrange
        Guid? analyseToken = inScenario ? Guid.NewGuid() : null;
        using var fixture = new PreCommitBatchParityFixture(analyseToken);

        // Act
        var together = await fixture.RestDayRotation.EvaluatePlannedRowsAsync(fixture.PlannedRows, analyseToken);
        var alone = new List<ScheduleValidationNotificationDto>();
        foreach (var group in fixture.PlannedRows.GroupBy(row => row.ClientId))
        {
            alone.AddRange(await fixture.RestDayRotation.EvaluatePlannedAsync(
                group.Key, string.Empty, group.Select(row => (row.Date, row.StartTime, row.EndTime)).ToList(), analyseToken));
        }

        // Assert
        together.ShouldNotBeEmpty();
        Identities(together).ShouldBe(Identities(alone));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task CounterRule_ForSeveralPeople_ReportsWhatEachPersonAloneReports(bool inScenario)
    {
        // Arrange
        Guid? analyseToken = inScenario ? Guid.NewGuid() : null;
        using var fixture = new PreCommitBatchParityFixture(analyseToken);

        // Act
        var together = await fixture.CounterRule.EvaluatePlannedRowsAsync(fixture.PlannedRows, analyseToken);
        var alone = new List<ScheduleValidationNotificationDto>();
        foreach (var group in fixture.PlannedRows.GroupBy(row => row.ClientId))
        {
            alone.AddRange(await fixture.CounterRule.EvaluatePlannedAsync(
                group.Key, string.Empty, group.Select(row => (row.Date, row.StartTime, row.EndTime)).ToList(), analyseToken));
        }

        // Assert
        together.ShouldNotBeEmpty();
        together.ShouldContain(finding => finding.Type == ScheduleValidationType.Error, "the rule with its own Block enforcement");
        together.ShouldContain(finding => finding.Type == ScheduleValidationType.Warning);
        Identities(together).ShouldBe(Identities(alone));
    }

    [Test]
    public async Task RestrictedTimeWindow_ForSeveralPeople_ReportsWhatEachPersonAloneReports()
    {
        // Arrange
        using var fixture = new PreCommitBatchParityFixture();

        // Act
        var together = await fixture.RestrictedTimeWindow.EvaluatePlannedRowsAsync(fixture.PlannedRows);
        var alone = new List<ScheduleValidationNotificationDto>();
        foreach (var group in fixture.PlannedRows.GroupBy(row => row.ClientId))
        {
            alone.AddRange(await fixture.RestrictedTimeWindow.EvaluatePlannedAsync(
                group.Key, string.Empty, group.Select(row => (row.Date, row.StartTime, row.EndTime, row.ShiftId)).ToList()));
        }

        // Assert
        together.Count(finding => finding.ClientId == PreCommitBatchParityFixture.Outdoor)
            .ShouldBe(2, "the rule for every shift and the rule for the outdoor group");
        Identities(together).ShouldBe(Identities(alone));
    }

    [Test]
    public async Task CompensatoryRest_ForSeveralPeople_ReportsWhatEachPersonAloneReports()
    {
        // Arrange
        using var fixture = new PreCommitBatchParityFixture();
        var clients = AsOfDates(fixture);

        // Act
        var together = await fixture.CompensatoryRest.EvaluateForClientsAsync(clients);
        var alone = new List<ScheduleValidationNotificationDto>();
        foreach (var client in clients)
        {
            alone.AddRange(await fixture.CompensatoryRest.EvaluateAsync(client.ClientId, string.Empty, client.AsOfDate));
        }

        // Assert
        together.Count.ShouldBe(3, "one open obligation of ShortRest and two of FullWeek");
        Identities(together).ShouldBe(Identities(alone));
    }

    [Test]
    public async Task CompensatoryRest_InAScenario_ReportsNothingForSeveralPeopleLikeForOne()
    {
        // Arrange
        var analyseToken = Guid.NewGuid();
        using var fixture = new PreCommitBatchParityFixture(analyseToken);

        // Act
        var together = await fixture.CompensatoryRest.EvaluateForClientsAsync(AsOfDates(fixture), analyseToken);
        var alone = await fixture.CompensatoryRest.EvaluateAsync(
            PreCommitBatchParityFixture.FullWeek, string.Empty, PreCommitBatchParityFixture.NextSunday, analyseToken);

        // Assert
        together.ShouldBeEmpty();
        alone.ShouldBeEmpty();
    }

    [Test]
    public async Task HolidayWork_ForSeveralPeople_ReportsWhatEachPersonAloneReports_AndAsksEachCalendarOnce()
    {
        // Arrange
        using var fixture = new PreCommitBatchParityFixture();
        var clients = fixture.PlannedRows
            .GroupBy(row => row.ClientId)
            .Select(group => new ClientWorkDates(
                group.Key,
                group.SelectMany(row => WorkedCalendarDates.FromTimeRange(row.Date, row.StartTime, row.EndTime)).Distinct().ToList()))
            .ToList();

        // Act
        var together = await fixture.HolidayWork.EvaluateForClientsAsync(clients);
        await fixture.HolidayCalendars.Received(1).GetCalculatorAsync(Arg.Is<Guid?>(id => id == null), YearOfThePlan);
        await fixture.HolidayCalendars.Received(1).GetCalculatorAsync(
            Arg.Is<Guid?>(id => id == PreCommitBatchParityFixture.HolidayCalendarId), YearOfThePlan);
        var alone = new List<ScheduleValidationNotificationDto>();
        foreach (var client in clients)
        {
            alone.AddRange(await fixture.HolidayWork.EvaluateAsync(client.ClientId, string.Empty, client.WorkDates));
        }

        // Assert
        together.Select(finding => (finding.ClientId, finding.Date)).ShouldBe(
        [
            (PreCommitBatchParityFixture.Lenient, PreCommitBatchParityFixture.Tuesday),
            (PreCommitBatchParityFixture.Outdoor, PreCommitBatchParityFixture.Thursday),
        ]);
        Identities(together).ShouldBe(Identities(alone));
    }

    private static List<ClientAsOfDate> AsOfDates(PreCommitBatchParityFixture fixture)
        => fixture.PlannedRows
            .GroupBy(row => row.ClientId)
            .Select(group => new ClientAsOfDate(group.Key, group.Max(row => row.Date)))
            .ToList();

    private static List<string> Identities(IEnumerable<ScheduleValidationNotificationDto> findings)
        => findings.Select(PreCommitBatchParityFixture.Identity).ToList();
}
