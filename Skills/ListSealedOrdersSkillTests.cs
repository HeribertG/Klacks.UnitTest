// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Application.DTOs.Exports;
using Klacks.Api.Application.Queries.Exports;
using Klacks.Api.Application.Skills;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class ListSealedOrdersSkillTests
{
    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanViewShifts" }
    };

    [Test]
    public async Task List_WithFilters_PassesQueryAndReturnsOrders()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<ListSealedOrdersQuery>(), Arg.Any<CancellationToken>())
            .Returns(new List<SealedOrderListItem>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    Abbreviation = "MIG-05",
                    Name = "Migros Mai",
                    FromDate = new DateOnly(2026, 5, 1),
                    UntilDate = new DateOnly(2026, 5, 31),
                    CustomerName = "Migros AG",
                    CustomerNumber = 1001,
                    TotalWorks = 20,
                    ClosedWorks = 20
                },
                new()
                {
                    Id = Guid.NewGuid(),
                    Abbreviation = "COOP-05",
                    Name = "Coop Mai",
                    FromDate = new DateOnly(2026, 5, 1),
                    TotalWorks = 10,
                    ClosedWorks = 4
                }
            });
        var skill = new ListSealedOrdersSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["fromDate"] = "2026-05-01",
            ["untilDate"] = "2026-05-31",
            ["search"] = "Mai"
        });

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("2 sealed order");
        await mediator.Received(1).Send(
            Arg.Is<ListSealedOrdersQuery>(q =>
                q.FromDate == new DateOnly(2026, 5, 1) &&
                q.UntilDate == new DateOnly(2026, 5, 31) &&
                q.CustomerId == null &&
                q.SearchTerm == "Mai"),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task List_Empty_ReturnsZeroCount()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<ListSealedOrdersQuery>(), Arg.Any<CancellationToken>())
            .Returns(new List<SealedOrderListItem>());
        var skill = new ListSealedOrdersSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("0 sealed order");
    }

    [Test]
    public async Task List_FromAfterUntil_ReturnsError()
    {
        var mediator = Substitute.For<IMediator>();
        var skill = new ListSealedOrdersSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["fromDate"] = "2026-06-01",
            ["untilDate"] = "2026-05-01"
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("fromDate must not be after untilDate");
        await mediator.DidNotReceive().Send(
            Arg.Any<ListSealedOrdersQuery>(), Arg.Any<CancellationToken>());
    }
}
