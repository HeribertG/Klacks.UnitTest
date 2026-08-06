// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for AddClientToNearestGroupSkill: with a start date the client is added to the
/// geographically nearest group, but when no start date is supplied the skill asks the user for one and
/// does not persist — the plannability boundary (ValidFrom) must never silently default to today.
/// </summary>

using System.Collections.ObjectModel;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Staffs;

using Klacks.Api.Application.DTOs.Associations;

using Klacks.Api.Infrastructure.Services.Assistant;

using Klacks.UnitTest.Infrastructure.SelfApi;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class AddClientToNearestGroupSkillTests
{
    private IClientRepository _clientRepository = null!;
    private IGroupRepository _groupRepository = null!;
    private IGroupItemRepository _groupItemRepository = null!;
    private FakeSelfApi _api = null!;
    private ICompanyClock _companyClock = null!;
    private AddClientToNearestGroupSkill _skill = null!;

    private static readonly Guid ClientId = Guid.NewGuid();
    private static readonly Guid BernGroupId = Guid.NewGuid();

    [SetUp]
    public void Setup()
    {
        _clientRepository = Substitute.For<IClientRepository>();
        _groupRepository = Substitute.For<IGroupRepository>();
        _groupItemRepository = Substitute.For<IGroupItemRepository>();
        _api = new FakeSelfApi();
        _api.Respond(HttpMethod.Post, "api/backend/GroupItems", new GroupItemResource());
        _companyClock = Substitute.For<ICompanyClock>();
        _companyClock.GetTodayAsync(Arg.Any<CancellationToken>())
            .Returns(new DateTime(2026, 6, 28, 0, 0, 0, DateTimeKind.Utc));
        _skill = new AddClientToNearestGroupSkill(
            _clientRepository, _groupRepository, TestGroupScopeGuard.Unrestricted(), _groupItemRepository, _api.Client, new SelfApiRouteResolver(), _companyClock);

        _clientRepository.Get(ClientId).Returns(new Client
        {
            Id = ClientId,
            FirstName = "Max",
            Name = "Müller",
            Addresses = new Collection<Address>
            {
                new() { Latitude = 46.948, Longitude = 7.447 }
            },
            GroupItems = new Collection<GroupItem>()
        });

        _groupRepository.List().Returns(new List<Group>
        {
            new() { Id = BernGroupId, Name = "Bern", Latitude = 46.948, Longitude = 7.447 }
        });
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanEditClients", "CanViewGroups" },
        AccessToken = new BearerToken("caller-jwt")
    };

    [TearDown]
    public void TearDown() => _api.Dispose();

    [Test]
    public async Task AddsClientToNearestGroup_WhenValidFromIsGiven()
    {
        var parameters = new Dictionary<string, object>
        {
            ["clientId"] = ClientId.ToString(),
            ["validFrom"] = "2026-05-01"
        };

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.True);
        _api.SingleCall.Method.ShouldBe(HttpMethod.Post);
    }

    [Test]
    public async Task AsksForStartDate_AndDoesNotPersist_WhenValidFromIsMissing()
    {
        var parameters = new Dictionary<string, object>
        {
            ["clientId"] = ClientId.ToString()
        };

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("date"));
        _api.Calls.ShouldBeEmpty();
    }
}
