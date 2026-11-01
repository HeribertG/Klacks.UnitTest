// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The uncovered-absence notice names the absence type in the schedule error list, but the three skills that hand
/// period findings to the language model (detect_conflicts, list_period_issues, evaluate_scenario) must not: the type
/// can reveal a health reason. Person, day and shift stay in the answer; the notice key still says what happened.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.DTOs.PeriodClosing;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Helpers;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Interfaces.PeriodClosing;
using Klacks.Api.Application.Queries.PeriodClosing;
using Klacks.Api.Application.Queries.Schedules;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.Api.Infrastructure.Services.Schedules;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class AbsenceTypeWithheldSkillTests
{
    private const string SickTypeDe = "Krankheit/Unfall";
    private const string SickTypeEn = "Illness/Accident";
    private const string ShiftLabel = "FD";
    private const string ClientName = "Muster Anna";
    private const string UserLanguage = "de";
    private const string UncoveredAbsenceCode = "UncoveredAbsence";
    private const string StartDate = "2026-03-02";
    private const string EndDate = "2026-03-08";

    private static readonly Guid GroupId = Guid.NewGuid();
    private static readonly Guid ScenarioToken = Guid.NewGuid();
    private static readonly DateOnly Tuesday = new(2026, 3, 3);

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserLanguage = UserLanguage,
        UserPermissions = new List<string> { "CanViewShifts" }
    };

    private static Dictionary<string, string> UncoveredAbsenceParams()
    {
        var parameters = new Dictionary<string, string> { [UncoveredAbsenceEntries.ShiftsParamKey] = ShiftLabel };
        var name = new MultiLanguage { De = SickTypeDe, En = SickTypeEn };
        LocalizedCommentParams.Add(parameters, LocalizedCommentParamKeys.Absence, name);
        return parameters;
    }

    private static PeriodIssueDto UncoveredAbsenceIssue() => new()
    {
        Date = Tuesday,
        ClientId = Guid.NewGuid(),
        ClientName = ClientName,
        Severity = ScheduleValidationType.Error,
        Code = UncoveredAbsenceCode,
        MessageKey = ScheduleValidationKeys.UncoveredAbsence,
        MessageParams = UncoveredAbsenceParams()
    };

    private static JsonElement Serialize(object? data) => JsonSerializer.SerializeToElement(data);

    private static void ShouldKeepEverythingButTheAbsenceType(JsonElement parameters, string wholeAnswer)
    {
        parameters.GetProperty(UncoveredAbsenceEntries.ShiftsParamKey).GetString().ShouldBe(ShiftLabel);
        parameters.TryGetProperty(LocalizedCommentParamKeys.Absence, out _).ShouldBeFalse();
        parameters.TryGetProperty(LocalizedCommentParamKeys.AbsenceMultiLanguage, out _).ShouldBeFalse();
        wholeAnswer.ShouldNotContain(SickTypeDe);
        wholeAnswer.ShouldNotContain(SickTypeEn);
        wholeAnswer.ShouldContain(ClientName);
    }

    [Test]
    public async Task DetectConflicts_UncoveredAbsence_KeepsPersonDayShiftAndWithholdsTheAbsenceType()
    {
        var loader = Substitute.For<IPeriodValidationLoader>();
        loader.LoadAsync(
                Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<Guid?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new List<PeriodIssueDto> { UncoveredAbsenceIssue() });
        var skill = new DetectConflictsSkill(
            loader, Substitute.For<IGroupRepository>(), TestGroupScopeGuard.Unrestricted(), new HiddenClientsVisibilityGuard());

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["groupId"] = GroupId.ToString(),
            ["fromDate"] = StartDate,
            ["untilDate"] = EndDate
        });

        result.Success.ShouldBeTrue(result.Message);
        var data = Serialize(result.Data);
        var conflict = data.GetProperty("Conflicts")[0];
        conflict.GetProperty("MessageKey").GetString().ShouldBe(ScheduleValidationKeys.UncoveredAbsence);
        conflict.GetProperty("Code").GetString().ShouldBe(UncoveredAbsenceCode);
        conflict.GetProperty("Date").GetString().ShouldBe("2026-03-03");
        ShouldKeepEverythingButTheAbsenceType(conflict.GetProperty("Params"), data.GetRawText() + result.Message);
    }

    [Test]
    public async Task ListPeriodIssues_UncoveredAbsence_KeepsPersonDayShiftAndWithholdsTheAbsenceType()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetPeriodIssuesQuery>(), Arg.Any<CancellationToken>())
            .Returns(new List<PeriodIssueDto> { UncoveredAbsenceIssue() });
        var skill = new ListPeriodIssuesSkill(mediator, Substitute.For<IGroupRepository>(), TestGroupScopeGuard.Unrestricted());

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["startDate"] = StartDate,
            ["endDate"] = EndDate
        });

        result.Success.ShouldBeTrue(result.Message);
        var data = Serialize(result.Data);
        var finding = data.GetProperty("Findings")[0];
        finding.GetProperty("MessageKey").GetString().ShouldBe(ScheduleValidationKeys.UncoveredAbsence);
        ShouldKeepEverythingButTheAbsenceType(finding.GetProperty("MessageParams"), data.GetRawText() + result.Message);
    }

    [Test]
    public async Task EvaluateScenario_UncoveredAbsence_KeepsPersonDayShiftAndWithholdsTheAbsenceType()
    {
        var conflict = new ScenarioConflictDto(
            Guid.NewGuid(), ClientName, "2026-03-03", "Error", UncoveredAbsenceCode,
            ScheduleValidationKeys.UncoveredAbsence, UncoveredAbsenceParams());
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<EvaluateScenarioQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ScenarioEvaluationResult(
                Found: true, ScenarioId: Guid.NewGuid(), Token: ScenarioToken, Name: "Proposal", Status: "Active",
                GroupId: GroupId, FromDate: StartDate, UntilDate: EndDate,
                TotalConflicts: 1, Errors: 1, Warnings: 0, Info: 0, RuleCompliant: false,
                ByCode: new Dictionary<string, int> { [UncoveredAbsenceCode] = 1 }, Conflicts: [conflict], ConflictsTruncated: false,
                RealEntryCount: 5, ScenarioEntryCount: 6, AddedEntryCount: 1, RemovedEntryCount: 0,
                AddedWorkEntries: 1, AddedReplacementEntries: 0, AddedBreakEntries: 0,
                AddedByType: new Dictionary<string, int> { ["Work"] = 1 }, AddedEntries: [], RemovedEntries: [],
                ChangesTruncated: false, Recommendation: "Resolve the error first."));
        var skill = new EvaluateScenarioSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["analyseToken"] = ScenarioToken.ToString()
        });

        result.Success.ShouldBeTrue(result.Message);
        var data = Serialize(result.Data);
        var returned = data.GetProperty("Conflicts")[0];
        returned.GetProperty("MessageKey").GetString().ShouldBe(ScheduleValidationKeys.UncoveredAbsence);
        ShouldKeepEverythingButTheAbsenceType(returned.GetProperty("MessageParams"), data.GetRawText() + result.Message);
    }
}
