// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests for GeocodeLocationGroupsSkill with geocoding switched off by the operator: it answers at once with a
/// clear message and neither resolves a single group nor queues the groups without coordinates.
/// </summary>

using Klacks.Api.Application.Constants;
using Klacks.Api.Application.Interfaces.Grouping;
using Klacks.Api.Application.Skills;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class GeocodeLocationGroupsSkillTests
{
    private IGroupRepository _groupRepository = null!;
    private IGroupLocationResolver _resolver = null!;
    private IGroupGeocodingQueue _queue = null!;
    private IGroupGeocoder _geocoder = null!;
    private GeocodeLocationGroupsSkill _skill = null!;

    [SetUp]
    public void Setup()
    {
        _groupRepository = Substitute.For<IGroupRepository>();
        _resolver = Substitute.For<IGroupLocationResolver>();
        _queue = Substitute.For<IGroupGeocodingQueue>();
        _geocoder = Substitute.For<IGroupGeocoder>();
        _groupRepository.List().Returns(new List<Group> { new() { Id = Guid.NewGuid(), Name = "Bern" } });
        _skill = new GeocodeLocationGroupsSkill(_groupRepository, _resolver, _queue, _geocoder);
    }

    [TestCase(null)]
    [TestCase("Bern")]
    public async Task Execute_GeocodingDisabled_ReportsItWithoutResolvingOrQueueing(string? groupName)
    {
        _geocoder.IsEnabled.Returns(false);
        var parameters = new Dictionary<string, object>();
        if (groupName != null)
        {
            parameters["groupName"] = groupName;
        }

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(GeoServiceMessages.GroupGeocodingDisabled);
        await _resolver.DidNotReceive().ResolveAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        _queue.DidNotReceive().Queue(Arg.Any<Guid>());
    }

    [Test]
    public async Task Execute_GeocodingEnabled_QueuesGroupsWithoutCoordinates()
    {
        _geocoder.IsEnabled.Returns(true);

        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Success.ShouldBeTrue();
        _queue.Received(1).Queue(Arg.Any<Guid>());
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanViewGroups" }
    };
}
