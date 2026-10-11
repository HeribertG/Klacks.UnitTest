// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests for the set_group_location skill with an operator-disabled geocoder: geocoding by name is answered with the
/// geocoding-disabled message (pointing to explicit coordinates) instead of "could not geocode", the geocoder is never
/// called, and explicit coordinates still set the location.
/// </summary>

using Klacks.Api.Application.Constants;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Interfaces.Grouping;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Associations;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class SetGroupLocationSkillTests
{
    private const string GroupName = "Bern";

    private IGroupRepository _groupRepository = null!;
    private IGroupScopeGuard _scopeGuard = null!;
    private IGroupGeocoder _geocoder = null!;
    private IKlacksSelfApiClient _selfApi = null!;
    private SetGroupLocationSkill _skill = null!;

    [SetUp]
    public void Setup()
    {
        _groupRepository = Substitute.For<IGroupRepository>();
        _groupRepository.List().Returns(new List<Group> { new() { Id = Guid.NewGuid(), Name = GroupName } });
        _scopeGuard = Substitute.For<IGroupScopeGuard>();
        _scopeGuard.GetAccessAsync(Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(GroupScopeAccess.Unrestricted());
        _geocoder = Substitute.For<IGroupGeocoder>();
        _selfApi = Substitute.For<IKlacksSelfApiClient>();
        _selfApi.PutAsync<object>(
                Arg.Any<string>(), Arg.Any<object>(), Arg.Any<SkillExecutionContext>(), Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(new SelfApiResult<object>(true, null, 200, null));
        _skill = new SetGroupLocationSkill(_groupRepository, _scopeGuard, _geocoder, _selfApi);
    }

    [Test]
    public async Task GeocodingDisabled_WithoutCoordinates_ReportsDisabledAndDoesNotGeocode()
    {
        _geocoder.IsEnabled.Returns(false);

        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object> { ["groupName"] = GroupName });

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(GeoServiceMessages.GroupLocationGeocodingDisabled);
        result.Message.ShouldStartWith(GeoServiceMessages.GeocodingDisabled);
        await _geocoder.DidNotReceiveWithAnyArgs().GeocodeAsync(default!, default);
        await _selfApi.DidNotReceiveWithAnyArgs().PutAsync<object>(default!, default!, default!, default!, default);
    }

    [Test]
    public async Task GeocodingDisabled_WithExplicitCoordinates_SetsTheLocation()
    {
        _geocoder.IsEnabled.Returns(false);
        var parameters = new Dictionary<string, object>
        {
            ["groupName"] = GroupName,
            ["latitude"] = 46.948,
            ["longitude"] = 7.447
        };

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        result.Success.ShouldBeTrue();
        await _geocoder.DidNotReceiveWithAnyArgs().GeocodeAsync(default!, default);
    }

    [Test]
    public async Task GeocodingEnabled_NoMatch_StillReportsCouldNotGeocode()
    {
        _geocoder.IsEnabled.Returns(true);
        (double? Latitude, double? Longitude) none = (null, null);
        _geocoder.GeocodeAsync(GroupName, Arg.Any<CancellationToken>()).Returns(none);

        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object> { ["groupName"] = GroupName });

        result.Success.ShouldBeFalse();
        result.Message!.ShouldContain("Could not geocode");
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string>()
    };
}
