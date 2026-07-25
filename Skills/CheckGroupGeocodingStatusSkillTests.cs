// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for CheckGroupGeocodingStatusSkill: the relayed message must state the pending count
/// and only hint at re-running the bulk geocode when something is actually still pending, and must
/// never leak internal skill names into the text.
/// </summary>

using Klacks.Api.Application.DTOs.Grouping;
using Klacks.Api.Application.Queries.Grouping;
using Klacks.Api.Application.Skills;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class CheckGroupGeocodingStatusSkillTests
{
    private IMediator _mediator = null!;

    [SetUp]
    public void Setup()
    {
        _mediator = Substitute.For<IMediator>();
    }

    [Test]
    public async Task Execute_SomeGroupsPending_MentionsPendingCount()
    {
        _mediator.Send(Arg.Any<GetGroupGeocodingStatusQuery>(), Arg.Any<CancellationToken>())
            .Returns(new GroupGeocodingStatus(65, 4, 10, 51));

        var result = await new CheckGroupGeocodingStatusSkill(_mediator).ExecuteAsync(Ctx(), NoParameters());

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("4 of 65");
        result.Message.ShouldContain("51 group(s) have not been processed yet");
    }

    [Test]
    public async Task Execute_NothingPending_SaysNoneAreStillPending()
    {
        _mediator.Send(Arg.Any<GetGroupGeocodingStatusQuery>(), Arg.Any<CancellationToken>())
            .Returns(new GroupGeocodingStatus(65, 60, 5, 0));

        var result = await new CheckGroupGeocodingStatusSkill(_mediator).ExecuteAsync(Ctx(), NoParameters());

        result.Message.ShouldContain("None are still pending.");
    }

    [Test]
    public async Task Execute_NeverLeaksInternalSkillNames()
    {
        _mediator.Send(Arg.Any<GetGroupGeocodingStatusQuery>(), Arg.Any<CancellationToken>())
            .Returns(new GroupGeocodingStatus(65, 4, 10, 51));

        var result = await new CheckGroupGeocodingStatusSkill(_mediator).ExecuteAsync(Ctx(), NoParameters());

        result.Message.ShouldNotContain("geocode_location_groups");
        result.Message.ShouldNotContain("check_group_geocoding_status");
    }

    private static Dictionary<string, object> NoParameters() => new();

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanViewGroups" }
    };
}
