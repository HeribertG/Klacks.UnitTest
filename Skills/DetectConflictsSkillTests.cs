// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the detect_conflicts skill: parameter validation, scenario-token pass-through to
/// the period validator, severity counts and projection of the violation list. The Data payload is
/// asserted via its serialized JSON shape because the projection uses internal anonymous types.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.DTOs.PeriodClosing;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Interfaces.PeriodClosing;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Assistant;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class DetectConflictsSkillTests
{
    private static readonly Guid GroupId = Guid.NewGuid();

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanViewShifts" }
    };

    private static PeriodIssueDto Issue(
        ScheduleValidationType severity,
        string code,
        string messageKey,
        string? clientName = null) => new()
        {
            Date = new DateOnly(2026, 3, 3),
            ClientId = Guid.NewGuid(),
            ClientName = clientName ?? "Anna",
            Severity = severity,
            Code = code,
            MessageKey = messageKey,
            MessageParams = new Dictionary<string, string> { ["actualHours"] = "8.0" }
        };

    private static IPeriodValidationLoader LoaderReturning(List<PeriodIssueDto> issues)
    {
        var loader = Substitute.For<IPeriodValidationLoader>();
        loader.LoadAsync(
                Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<Guid?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(issues);
        return loader;
    }

    private static Dictionary<string, object> Params(string? analyseToken = null)
    {
        var p = new Dictionary<string, object>
        {
            ["groupId"] = GroupId.ToString(),
            ["fromDate"] = "2026-03-02",
            ["untilDate"] = "2026-03-08"
        };
        if (analyseToken != null)
        {
            p["analyseToken"] = analyseToken;
        }
        return p;
    }

    private static JsonElement DataAsJson(SkillResult result)
        => JsonSerializer.SerializeToElement(result.Data);

    [Test]
    public async Task Conflicts_ReturnsSeverityCountsAndProjection()
    {
        var issues = new List<PeriodIssueDto>
        {
            Issue(ScheduleValidationType.Error, "Collision", "schedule.error-list.collision"),
            Issue(ScheduleValidationType.Warning, "RestViolation", "schedule.error-list.rest-violation"),
            Issue(ScheduleValidationType.Warning, "WeeklyOvertime", "schedule.error-list.weekly-overtime")
        };
        var skill = new DetectConflictsSkill(LoaderReturning(issues));

        var result = await skill.ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeTrue();
        var data = DataAsJson(result);
        data.GetProperty("TotalConflicts").GetInt32().ShouldBe(3);
        data.GetProperty("Errors").GetInt32().ShouldBe(1);
        data.GetProperty("Warnings").GetInt32().ShouldBe(2);
        data.GetProperty("IsScenario").GetBoolean().ShouldBeFalse();
        data.GetProperty("ByCode").GetProperty("WeeklyOvertime").GetInt32().ShouldBe(1);
        data.GetProperty("Conflicts").GetArrayLength().ShouldBe(3);

        var first = data.GetProperty("Conflicts")[0];
        first.GetProperty("Severity").GetString().ShouldBe("Error");
        first.GetProperty("Code").GetString().ShouldBe("Collision");
        first.GetProperty("MessageKey").GetString().ShouldBe("schedule.error-list.collision");
        first.GetProperty("Date").GetString().ShouldBe("2026-03-03");
        first.GetProperty("Params").GetProperty("actualHours").GetString().ShouldBe("8.0");
    }

    [Test]
    public async Task NoConflicts_ReturnsEmptySuccess()
    {
        var skill = new DetectConflictsSkill(LoaderReturning(new List<PeriodIssueDto>()));

        var result = await skill.ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeTrue();
        DataAsJson(result).GetProperty("TotalConflicts").GetInt32().ShouldBe(0);
        result.Message.ShouldContain("No conflicts");
    }

    [Test]
    public async Task ScenarioToken_PassedThroughToLoader()
    {
        var token = Guid.NewGuid();
        var loader = LoaderReturning(new List<PeriodIssueDto>());
        var skill = new DetectConflictsSkill(loader);

        var result = await skill.ExecuteAsync(Ctx(), Params(token.ToString()));

        result.Success.ShouldBeTrue();
        DataAsJson(result).GetProperty("IsScenario").GetBoolean().ShouldBeTrue();
        await loader.Received(1).LoadAsync(
            Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), GroupId, token, Arg.Any<int?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RealMode_PassesNullTokenToLoader()
    {
        var loader = LoaderReturning(new List<PeriodIssueDto>());
        var skill = new DetectConflictsSkill(loader);

        await skill.ExecuteAsync(Ctx(), Params());

        await loader.Received(1).LoadAsync(
            Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), GroupId, (Guid?)null, Arg.Any<int?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task InvalidFromDate_ReturnsError()
    {
        var skill = new DetectConflictsSkill(LoaderReturning(new List<PeriodIssueDto>()));
        var p = Params();
        p["fromDate"] = "not-a-date";

        var result = await skill.ExecuteAsync(Ctx(), p);

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("fromDate");
    }

    [Test]
    public async Task UntilBeforeFrom_ReturnsError()
    {
        var skill = new DetectConflictsSkill(LoaderReturning(new List<PeriodIssueDto>()));
        var p = Params();
        p["untilDate"] = "2026-03-01";

        var result = await skill.ExecuteAsync(Ctx(), p);

        result.Success.ShouldBeFalse();
    }

    [Test]
    public async Task InvalidAnalyseToken_ReturnsError()
    {
        var skill = new DetectConflictsSkill(LoaderReturning(new List<PeriodIssueDto>()));

        var result = await skill.ExecuteAsync(Ctx(), Params("not-a-guid"));

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("analyseToken");
    }
}
