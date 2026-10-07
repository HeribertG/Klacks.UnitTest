// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for the thin cover_absence skill: it validates the required ids/date, delegates to
/// ICoverAbsenceService and projects the outcome (covered / uncovered). Orchestration is covered by
/// CoverAbsenceServiceTests.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Commands.Schedules;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class CoverAbsenceSkillTests
{
    private static readonly Guid ClientId = Guid.NewGuid();
    private static readonly Guid GroupId = Guid.NewGuid();
    private static readonly Guid AbsenceId = Guid.NewGuid();
    private static readonly Guid ShiftId = Guid.NewGuid();

    private IMediator _mediator = null!;

    [SetUp]
    public void Setup()
    {
        _mediator = Substitute.For<IMediator>();
        _mediator.Send(Arg.Any<CoverAbsenceCommand>(), Arg.Any<CancellationToken>())
            .Returns(new CoverAbsenceOutcome(
                Guid.NewGuid(), Guid.NewGuid(), "Absence cover 10.03.26",
                new List<CoveredSlot> { new(ShiftId, new DateOnly(2026, 3, 10), Guid.NewGuid(), "Bob") },
                new List<UncoveredSlot> { new(Guid.NewGuid(), new DateOnly(2026, 3, 10), "locked") },
                new List<Klacks.Api.Application.DTOs.Notifications.ScheduleValidationNotificationDto>()));
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanEditShifts" }
    };

    private CoverAbsenceSkill Skill() => new(_mediator);

    private static Dictionary<string, object> Params() => new()
    {
        ["clientId"] = ClientId.ToString(),
        ["date"] = new DateOnly(2026, 3, 10),
        ["groupId"] = GroupId.ToString(),
        ["absenceId"] = AbsenceId.ToString()
    };

    private static JsonElement DataAsJson(SkillResult result)
        => JsonSerializer.SerializeToElement(result.Data);

    [Test]
    public async Task DelegatesToService_AndProjects()
    {
        var result = await Skill().ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeTrue();
        var data = DataAsJson(result);
        data.GetProperty("CoveredCount").GetInt32().ShouldBe(1);
        data.GetProperty("UncoveredCount").GetInt32().ShouldBe(1);
        data.GetProperty("Covered").EnumerateArray().Single()
            .GetProperty("ReplacementName").GetString().ShouldBe("Bob");

        await _mediator.Received(1).Send(
            Arg.Is<CoverAbsenceCommand>(c =>
                c.ClientId == ClientId && c.Date == new DateOnly(2026, 3, 10) && c.GroupId == GroupId && c.AbsenceId == AbsenceId),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task TheUsersLanguage_IsPassedOn_ForTheScenarioName()
    {
        var context = Ctx() with { UserLanguage = "nl" };

        await Skill().ExecuteAsync(context, Params());

        await _mediator.Received(1).Send(
            Arg.Is<CoverAbsenceCommand>(c => c.Language == "nl"), Arg.Any<CancellationToken>());
    }

    [Test]
    public void MissingAbsenceId_Throws()
    {
        var p = Params();
        p.Remove("absenceId");

        Should.Throw<ArgumentException>(async () => await Skill().ExecuteAsync(Ctx(), p));
    }

    [Test]
    public async Task InboundContext_PassesReceiveTimeAndMessengerSource()
    {
        var receivedAt = new DateTime(2026, 3, 10, 5, 12, 0, DateTimeKind.Utc);

        await Skill().ExecuteAsync(Ctx() with { InboundReceivedAtUtc = receivedAt }, Params());

        await _mediator.Received(1).Send(
            Arg.Is<CoverAbsenceCommand>(c => c.ReportedAtUtc == receivedAt && c.Source == ReplacementRequestSource.Messenger),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ChatCaller_IgnoresSourceAndReportTimeSuppliedByTheModel()
    {
        var parameters = Params();
        parameters["source"] = ReplacementRequestSource.Messenger.ToString();
        parameters["reportedAtUtc"] = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

        await Skill().ExecuteAsync(Ctx(), parameters);

        await _mediator.Received(1).Send(
            Arg.Is<CoverAbsenceCommand>(c => c.Source == ReplacementRequestSource.RecoveryEngine && c.ReportedAtUtc == null),
            Arg.Any<CancellationToken>());
    }
    [Test]
    public async Task Projection_NeverExposesThePhoneNumber()
    {
        _mediator.Send(Arg.Any<CoverAbsenceCommand>(), Arg.Any<CancellationToken>())
            .Returns(new CoverAbsenceOutcome(
                Guid.NewGuid(), Guid.NewGuid(), "Absence cover",
                new List<CoveredSlot> { new(ShiftId, new DateOnly(2026, 3, 10), Guid.NewGuid(), "Bob", Phone: "079 555 55 55") },
                new List<UncoveredSlot>(),
                new List<Klacks.Api.Application.DTOs.Notifications.ScheduleValidationNotificationDto>()));

        var result = await Skill().ExecuteAsync(Ctx(), Params());

        JsonSerializer.Serialize(result.Data).ShouldNotContain("079 555 55 55");
    }
}