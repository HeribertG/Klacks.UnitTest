// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for set_client_availability: a date range expands to one slot per day and hour
/// (full day by default), explicit single dates with an hour window build only those slots,
/// and invalid hour windows or oversized ranges are rejected without touching the mediator.
/// </summary>

using Klacks.Api.Application.Commands.ClientAvailabilities;
using Klacks.Api.Application.DTOs.Staffs;
using Klacks.Api.Application.Skills;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class SetClientAvailabilitySkillTests
{
    private static readonly Guid ClientId = Guid.NewGuid();

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanEditClients" }
    };

    [Test]
    public async Task SetRange_DefaultHours_BuildsFullDaySlotsForEachDay()
    {
        var mediator = Substitute.For<IMediator>();
        ClientAvailabilityBulkRequest? captured = null;
        mediator.Send(Arg.Do<BulkUpdateClientAvailabilityCommand>(c => captured = c.Request), Arg.Any<CancellationToken>())
            .Returns(ci => ((BulkUpdateClientAvailabilityCommand)ci[0]).Request.Items.Count);
        var skill = new SetClientAvailabilitySkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["clientId"] = ClientId.ToString(),
            ["startDate"] = "2026-06-15",
            ["endDate"] = "2026-06-16",
            ["isAvailable"] = false
        });

        result.Success.ShouldBeTrue();
        captured.ShouldNotBeNull();
        captured!.Items.Count.ShouldBe(48);
        captured.Items.ShouldAllBe(i => i.ClientId == ClientId && !i.IsAvailable);
        captured.Items.Count(i => i.Date == new DateOnly(2026, 6, 15)).ShouldBe(24);
        captured.Items.Count(i => i.Date == new DateOnly(2026, 6, 16)).ShouldBe(24);
        captured.Items.Min(i => i.Hour).ShouldBe(0);
        captured.Items.Max(i => i.Hour).ShouldBe(23);
        result.Message.ShouldContain("unavailable");
    }

    [Test]
    public async Task SetSingleDates_HourWindow_BuildsOnlyRequestedSlots()
    {
        var mediator = Substitute.For<IMediator>();
        ClientAvailabilityBulkRequest? captured = null;
        mediator.Send(Arg.Do<BulkUpdateClientAvailabilityCommand>(c => captured = c.Request), Arg.Any<CancellationToken>())
            .Returns(ci => ((BulkUpdateClientAvailabilityCommand)ci[0]).Request.Items.Count);
        var skill = new SetClientAvailabilitySkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["clientId"] = ClientId.ToString(),
            ["dates"] = "2026-06-15, 2026-06-17",
            ["startHour"] = 8,
            ["endHour"] = 10,
            ["isAvailable"] = true
        });

        result.Success.ShouldBeTrue();
        captured.ShouldNotBeNull();
        captured!.Items.Count.ShouldBe(6);
        captured.Items.ShouldAllBe(i => i.IsAvailable && i.Hour >= 8 && i.Hour <= 10);
        captured.Items.Select(i => i.Date).Distinct().Count().ShouldBe(2);
        result.Message.ShouldContain("available");
    }

    [Test]
    public async Task SetAvailability_InvalidHourWindow_ReturnsError_NoMutation()
    {
        var mediator = Substitute.For<IMediator>();
        var skill = new SetClientAvailabilitySkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["clientId"] = ClientId.ToString(),
            ["startDate"] = "2026-06-15",
            ["startHour"] = 20,
            ["endHour"] = 5,
            ["isAvailable"] = true
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("hour");
        await mediator.DidNotReceive().Send(
            Arg.Any<BulkUpdateClientAvailabilityCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SetAvailability_RangeTooLarge_ReturnsError_NoMutation()
    {
        var mediator = Substitute.For<IMediator>();
        var skill = new SetClientAvailabilitySkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["clientId"] = ClientId.ToString(),
            ["startDate"] = "2026-01-01",
            ["endDate"] = "2026-12-31",
            ["isAvailable"] = true
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("range");
        await mediator.DidNotReceive().Send(
            Arg.Any<BulkUpdateClientAvailabilityCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SetAvailability_InvalidDateInList_ReturnsError_NoMutation()
    {
        var mediator = Substitute.For<IMediator>();
        var skill = new SetClientAvailabilitySkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["clientId"] = ClientId.ToString(),
            ["dates"] = "2026-06-15, not-a-date",
            ["isAvailable"] = true
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("not-a-date");
        await mediator.DidNotReceive().Send(
            Arg.Any<BulkUpdateClientAvailabilityCommand>(), Arg.Any<CancellationToken>());
    }
}
