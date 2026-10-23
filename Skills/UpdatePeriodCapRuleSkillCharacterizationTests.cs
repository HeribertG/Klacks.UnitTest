// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Characterization tests for UpdatePeriodCapRuleSkill.ExecuteAsync, written before its long body was split into
/// private steps: the not-found, invalid-period/-scope, no-op, rejected and empty-result texts, the success text and
/// serialized result data, the order of the changed fields, the clearing of customPeriodWeeks when the window leaves
/// CustomWeeks, and that a period change already applied to the loaded resource stays applied when the scope is invalid.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Scheduling;
using Klacks.Api.Application.Queries;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class UpdatePeriodCapRuleSkillCharacterizationTests
{
    private static readonly Guid RuleId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private IMediator _mediator = null!;
    private UpdatePeriodCapRuleSkill _skill = null!;
    private PeriodCapRuleResource _existing = null!;
    private PeriodCapRuleResource? _sent;

    [SetUp]
    public void Setup()
    {
        _sent = null;
        _existing = new PeriodCapRuleResource
        {
            Id = RuleId,
            Period = PeriodCapPeriod.Month,
            Scope = PeriodCapScope.TotalHours,
            CapHours = 160m,
            WarnAtPercent = 80
        };

        _mediator = Substitute.For<IMediator>();
        _mediator.Send(Arg.Any<GetQuery<PeriodCapRuleResource>>(), Arg.Any<CancellationToken>())
            .Returns(_ => _existing);
        _mediator.Send(Arg.Any<PutCommand<PeriodCapRuleResource>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                _sent = ci.Arg<PutCommand<PeriodCapRuleResource>>().Resource;
                return _sent;
            });

        _skill = new UpdatePeriodCapRuleSkill(_mediator);
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "Admin" }
    };

    private static Dictionary<string, object> Params(params (string Key, object Value)[] values)
    {
        var parameters = new Dictionary<string, object> { ["periodCapRuleId"] = RuleId.ToString() };
        foreach (var (key, value) in values)
        {
            parameters[key] = value;
        }

        return parameters;
    }

    [Test]
    public async Task UnknownRule_ReturnsNotFound_WithoutPut()
    {
        _mediator.Send(Arg.Any<GetQuery<PeriodCapRuleResource>>(), Arg.Any<CancellationToken>())
            .Returns<PeriodCapRuleResource>(_ => throw new KeyNotFoundException());

        var result = await _skill.ExecuteAsync(Ctx(), Params(("capHours", 100m)));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe($"Period cap rule {RuleId} not found.");
        await _mediator.DidNotReceive().Send(Arg.Any<PutCommand<PeriodCapRuleResource>>(), Arg.Any<CancellationToken>());
    }

    [TestCase("Weekly")]
    [TestCase("7")]
    public async Task InvalidPeriod_ListsTheValidNames(string raw)
    {
        var result = await _skill.ExecuteAsync(Ctx(), Params(("period", raw), ("capHours", 100m)));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe($"Invalid period '{raw}'. Use one of: Month, Quarter, Year, CustomWeeks.");
        _existing.CapHours.ShouldBe(160m);
        await _mediator.DidNotReceive().Send(Arg.Any<PutCommand<PeriodCapRuleResource>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task InvalidScope_AfterAValidPeriodChange_KeepsThePeriodAppliedToTheLoadedRule()
    {
        var result = await _skill.ExecuteAsync(Ctx(), Params(("period", "quarter"), ("scope", "Night")));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe("Invalid scope 'Night'. Use one of: TotalHours, OvertimeHours.");
        _existing.Period.ShouldBe(PeriodCapPeriod.Quarter);
        await _mediator.DidNotReceive().Send(Arg.Any<PutCommand<PeriodCapRuleResource>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task LeavingCustomWeeks_ClearsCustomPeriodWeeks()
    {
        _existing.Period = PeriodCapPeriod.CustomWeeks;
        _existing.CustomPeriodWeeks = 6;

        var result = await _skill.ExecuteAsync(Ctx(), Params(("period", "year")));

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldBe($"Period cap rule {RuleId} updated (period, customPeriodWeeks).");
        _sent.ShouldNotBeNull();
        _sent!.Period.ShouldBe(PeriodCapPeriod.Year);
        _sent.CustomPeriodWeeks.ShouldBeNull();
    }

    [Test]
    public async Task LeavingCustomWeeks_WithNewWeeks_SetsTheWeeksAgain_AndListsTheFieldOnce()
    {
        _existing.Period = PeriodCapPeriod.CustomWeeks;
        _existing.CustomPeriodWeeks = 6;

        var result = await _skill.ExecuteAsync(Ctx(), Params(("period", "Month"), ("customPeriodWeeks", 4)));

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldBe($"Period cap rule {RuleId} updated (period, customPeriodWeeks).");
        _sent!.Period.ShouldBe(PeriodCapPeriod.Month);
        _sent.CustomPeriodWeeks.ShouldBe(4);
    }

    [Test]
    public async Task AllFieldsChanged_ReportsThemInFixedOrder_WithTheUpdatedData()
    {
        var result = await _skill.ExecuteAsync(Ctx(), Params(
            ("maxAverageWeeklyHours", 48m),
            ("rollingWindowWeeks", 17),
            ("customPeriodWeeks", 4),
            ("warnAtPercent", 90),
            ("capHours", 120m),
            ("scope", "overtimehours")));

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldBe(
            $"Period cap rule {RuleId} updated (scope, capHours, warnAtPercent, customPeriodWeeks, rollingWindowWeeks, maxAverageWeeklyHours).");
        JsonSerializer.Serialize(result.Data).ShouldBe(
            "{\"PeriodCapRuleId\":\"" + RuleId + "\","
            + "\"ChangedFields\":[\"scope\",\"capHours\",\"warnAtPercent\",\"customPeriodWeeks\",\"rollingWindowWeeks\",\"maxAverageWeeklyHours\"],"
            + "\"Period\":\"Month\",\"Scope\":\"OvertimeHours\",\"CapHours\":120,\"WarnAtPercent\":90,"
            + "\"CustomPeriodWeeks\":4,\"RollingWindowWeeks\":17,\"MaxAverageWeeklyHours\":48}");
        await _mediator.Received(1).Send(
            Arg.Is<PutCommand<PeriodCapRuleResource>>(c => ReferenceEquals(c.Resource, _existing)),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UnchangedValues_ReturnNoOp_WithoutPut()
    {
        var result = await _skill.ExecuteAsync(Ctx(), Params(
            ("period", "month"),
            ("scope", "TotalHours"),
            ("capHours", 160m),
            ("warnAtPercent", 80)));

        result.Success.ShouldBeTrue();
        result.Message.ShouldBe("No fields supplied for update — period cap rule left unchanged.");
        JsonSerializer.Serialize(result.Data).ShouldBe(
            "{\"PeriodCapRuleId\":\"" + RuleId + "\",\"ChangedFields\":[]}");
        await _mediator.DidNotReceive().Send(Arg.Any<PutCommand<PeriodCapRuleResource>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RejectedPut_RelaysTheHandlerMessage()
    {
        _mediator.Send(Arg.Any<PutCommand<PeriodCapRuleResource>>(), Arg.Any<CancellationToken>())
            .Returns<PeriodCapRuleResource?>(_ => throw new InvalidRequestException("Cap hours must be positive."));

        var result = await _skill.ExecuteAsync(Ctx(), Params(("capHours", 0m)));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe("Cap hours must be positive.");
    }

    [Test]
    public async Task PutWithoutResult_ReturnsError()
    {
        _mediator.Send(Arg.Any<PutCommand<PeriodCapRuleResource>>(), Arg.Any<CancellationToken>())
            .Returns((PeriodCapRuleResource?)null);

        var result = await _skill.ExecuteAsync(Ctx(), Params(("warnAtPercent", 95)));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(
            $"Update of period cap rule {RuleId} returned no result — operation may have failed.");
    }
}
