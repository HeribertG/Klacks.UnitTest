// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for get_client_locations_overview: the skill sends GetClientLocationsQuery,
/// groups locations by type, country and city, counts geocoded entries, supports the
/// clientType filter and rejects unknown clientType values.
/// </summary>

using Klacks.Api.Application.DTOs.Dashboard;
using Klacks.Api.Application.Queries.Dashboard;
using Klacks.Api.Application.Skills;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class GetClientLocationsOverviewSkillTests
{
    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanViewClients" }
    };

    private static List<ClientLocationResource> SampleLocations() =>
    [
        new()
        {
            Id = Guid.NewGuid().ToString(),
            Type = (int)EntityTypeEnum.Employee,
            CurrentAddress = new AddressInfo
            {
                City = "Bern",
                Country = "Schweiz",
                Zip = "3000",
                Latitude = 46.948,
                Longitude = 7.4474
            }
        },
        new()
        {
            Id = Guid.NewGuid().ToString(),
            Type = (int)EntityTypeEnum.Employee,
            CurrentAddress = new AddressInfo
            {
                City = "Bern",
                Country = "Schweiz",
                Zip = "3011",
                Latitude = 46.948,
                Longitude = 7.4474
            }
        },
        new()
        {
            Id = Guid.NewGuid().ToString(),
            Type = (int)EntityTypeEnum.Customer,
            CurrentAddress = new AddressInfo
            {
                City = "Zürich",
                Country = "Schweiz",
                Zip = "8000",
                Latitude = null,
                Longitude = null
            }
        }
    ];

    [Test]
    public async Task Overview_GroupsByTypeCountryAndCity()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetClientLocationsQuery>(), Arg.Any<CancellationToken>())
            .Returns(SampleLocations());
        var skill = new GetClientLocationsOverviewSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Success.ShouldBeTrue();
        result.Message.ShouldNotBeNull();
        result.Message.ShouldContain("Found 3 client location(s)");
        result.Message.ShouldContain("2 geocoded");
        await mediator.Received(1).Send(
            Arg.Any<GetClientLocationsQuery>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Overview_ClientTypeFilter_CountsOnlyMatchingType()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetClientLocationsQuery>(), Arg.Any<CancellationToken>())
            .Returns(SampleLocations());
        var skill = new GetClientLocationsOverviewSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["clientType"] = "Employee"
        });

        result.Success.ShouldBeTrue();
        result.Message.ShouldNotBeNull();
        result.Message.ShouldContain("Found 2 client location(s)");
        result.Message.ShouldContain("of type Employee");
    }

    [Test]
    public async Task Overview_InvalidClientType_ReturnsError()
    {
        var mediator = Substitute.For<IMediator>();
        var skill = new GetClientLocationsOverviewSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["clientType"] = "alien"
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldNotBeNull();
        result.Message.ShouldContain("Invalid clientType");
        await mediator.DidNotReceive().Send(
            Arg.Any<GetClientLocationsQuery>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Overview_Empty_ReturnsZeroCounts()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetClientLocationsQuery>(), Arg.Any<CancellationToken>())
            .Returns(new List<ClientLocationResource>());
        var skill = new GetClientLocationsOverviewSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Success.ShouldBeTrue();
        result.Message.ShouldNotBeNull();
        result.Message.ShouldContain("Found 0 client location(s)");
    }
}
