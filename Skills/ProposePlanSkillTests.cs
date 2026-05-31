// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the propose_plan skill: it parses the placements JSON, validates dates/ids and the
/// in-period constraint, delegates to IProposePlanService and projects the outcome. Data asserted via
/// its serialized JSON shape.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Commands.Schedules;
using Klacks.Api.Application.DTOs.Notifications;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class ProposePlanSkillTests
{
    private static readonly Guid GroupId = Guid.NewGuid();
    private static readonly Guid ClientId = Guid.NewGuid();
    private static readonly Guid ShiftId = Guid.NewGuid();

    private IMediator _mediator = null!;

    [SetUp]
    public void Setup()
    {
        _mediator = Substitute.For<IMediator>();
        _mediator.Send(Arg.Any<ProposePlanCommand>(), Arg.Any<CancellationToken>())
            .Returns(new ProposePlanOutcome(
                Guid.NewGuid(), Guid.NewGuid(), "Proposal 02.03.26 – 08.03.26",
                new List<PlacementInput> { new(ClientId, ShiftId, new DateOnly(2026, 3, 3)) },
                new List<RejectedPlacement>(),
                new List<ScheduleValidationNotificationDto>()));
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanEditShifts" }
    };

    private ProposePlanSkill Skill() => new(_mediator);

    private static Dictionary<string, object> Params(string placementsJson) => new()
    {
        ["groupId"] = GroupId.ToString(),
        ["fromDate"] = "2026-03-02",
        ["untilDate"] = "2026-03-08",
        ["placements"] = placementsJson
    };

    private static string OnePlacement(string date = "2026-03-03")
        => $"[{{\"clientId\":\"{ClientId}\",\"shiftId\":\"{ShiftId}\",\"date\":\"{date}\"}}]";

    private static JsonElement DataAsJson(SkillResult result)
        => JsonSerializer.SerializeToElement(result.Data);

    [Test]
    public async Task ValidPlacements_DelegatesAndProjects()
    {
        var result = await Skill().ExecuteAsync(Ctx(), Params(OnePlacement()));

        result.Success.ShouldBeTrue();
        var data = DataAsJson(result);
        data.GetProperty("WrittenCount").GetInt32().ShouldBe(1);
        data.GetProperty("ScenarioName").GetString().ShouldBe("Proposal 02.03.26 – 08.03.26");

        await _mediator.Received(1).Send(
            Arg.Is<ProposePlanCommand>(c => c.Placements.Count == 1 && c.Placements[0].ClientId == ClientId),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task InvalidJson_ReturnsError()
    {
        var result = await Skill().ExecuteAsync(Ctx(), Params("not-json"));

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("placements");
    }

    [Test]
    public async Task EmptyPlacements_ReturnsError()
    {
        var result = await Skill().ExecuteAsync(Ctx(), Params("[]"));

        result.Success.ShouldBeFalse();
    }

    [Test]
    public async Task PlacementDateOutsidePeriod_ReturnsError()
    {
        var result = await Skill().ExecuteAsync(Ctx(), Params(OnePlacement("2026-04-01")));

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("outside the period");
    }

    [Test]
    public async Task InvalidClientId_ReturnsError()
    {
        var bad = $"[{{\"clientId\":\"nope\",\"shiftId\":\"{ShiftId}\",\"date\":\"2026-03-03\"}}]";

        var result = await Skill().ExecuteAsync(Ctx(), Params(bad));

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("clientId");
    }

    [Test]
    public async Task UntilBeforeFrom_ReturnsError()
    {
        var p = Params(OnePlacement());
        p["untilDate"] = "2026-03-01";

        var result = await Skill().ExecuteAsync(Ctx(), p);

        result.Success.ShouldBeFalse();
    }
}
