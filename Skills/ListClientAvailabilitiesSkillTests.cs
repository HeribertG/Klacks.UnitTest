// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for list_client_availabilities: the skill forwards the date range to
/// ListClientAvailabilitiesQuery and returns the entries; an inverted range is rejected
/// without touching the mediator.
/// </summary>

using Klacks.Api.Application.DTOs.Staffs;
using Klacks.Api.Application.Queries.ClientAvailabilities;
using Klacks.Api.Application.Skills;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class ListClientAvailabilitiesSkillTests
{
    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanViewClients" }
    };

    [Test]
    public async Task List_ValidRange_SendsQueryAndReturnsEntries()
    {
        var mediator = Substitute.For<IMediator>();
        var clientId = Guid.NewGuid();
        mediator.Send(Arg.Any<ListClientAvailabilitiesQuery>(), Arg.Any<CancellationToken>())
            .Returns(new List<ClientAvailabilityResource>
            {
                new() { Id = Guid.NewGuid(), ClientId = clientId, Date = new DateOnly(2026, 6, 15), Hour = 8, IsAvailable = true },
                new() { Id = Guid.NewGuid(), ClientId = clientId, Date = new DateOnly(2026, 6, 15), Hour = 9, IsAvailable = false }
            });
        var skill = new ListClientAvailabilitiesSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["startDate"] = "2026-06-15",
            ["endDate"] = "2026-06-21"
        });

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("2 availability entries");
        await mediator.Received(1).Send(
            Arg.Is<ListClientAvailabilitiesQuery>(q =>
                q.StartDate == new DateOnly(2026, 6, 15) && q.EndDate == new DateOnly(2026, 6, 21)),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task List_EndDateBeforeStartDate_ReturnsError_NoQuery()
    {
        var mediator = Substitute.For<IMediator>();
        var skill = new ListClientAvailabilitiesSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["startDate"] = "2026-06-21",
            ["endDate"] = "2026-06-15"
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("endDate");
        await mediator.DidNotReceive().Send(
            Arg.Any<ListClientAvailabilitiesQuery>(), Arg.Any<CancellationToken>());
    }
}
