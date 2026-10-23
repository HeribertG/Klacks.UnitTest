// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Characterization tests for AddClientToNearestGroupSkill.ExecuteAsync, written before its long body was split into
/// resolution and write steps: every refusal and "leave it" text with its result data, the order of the checks (the
/// date is read before the client is loaded, an existing membership in the nearest group wins over a missing start
/// date), the nearest of several geocoded groups is chosen, and the posted membership and success data.
/// </summary>

using System.Collections.ObjectModel;
using System.Net;
using System.Text.Json;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Staffs;

using Klacks.Api.Application.DTOs.Associations;

using Klacks.Api.Infrastructure.Services.Assistant;

using Klacks.UnitTest.Infrastructure.SelfApi;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class AddClientToNearestGroupSkillCharacterizationTests
{
    private const string GroupItemsRoute = "api/backend/GroupItems";

    private static readonly Guid ClientId = Guid.NewGuid();
    private static readonly Guid BernGroupId = Guid.NewGuid();
    private static readonly Guid ZurichGroupId = Guid.NewGuid();

    private IClientRepository _clientRepository = null!;
    private IGroupRepository _groupRepository = null!;
    private FakeSelfApi _api = null!;
    private AddClientToNearestGroupSkill _skill = null!;
    private Client _client = null!;

    [SetUp]
    public void Setup()
    {
        _clientRepository = Substitute.For<IClientRepository>();
        _groupRepository = Substitute.For<IGroupRepository>();
        _api = new FakeSelfApi();
        _api.Respond(HttpMethod.Post, GroupItemsRoute, new GroupItemResource());
        var companyClock = Substitute.For<ICompanyClock>();
        companyClock.GetTodayAsync(Arg.Any<CancellationToken>())
            .Returns(new DateTime(2026, 6, 28, 0, 0, 0, DateTimeKind.Utc));
        _skill = new AddClientToNearestGroupSkill(
            _clientRepository, SkillClientVisibility.AllVisible(), _groupRepository, TestGroupScopeGuard.Unrestricted(),
            Substitute.For<IGroupItemRepository>(), _api.Client, new SelfApiRouteResolver(), companyClock,
            TestGroupMembershipStart.Planner(companyClock));

        _client = new Client
        {
            Id = ClientId,
            FirstName = "Max",
            Name = "Müller",
            Addresses = new Collection<Address>
            {
                new() { Latitude = 47.0, Longitude = 8.0, IsDeleted = true },
                new() { Latitude = 46.948, Longitude = 7.447 }
            },
            GroupItems = new Collection<GroupItem>()
        };
        _clientRepository.Get(ClientId).Returns(_client);

        _groupRepository.List().Returns(new List<Group>
        {
            new() { Id = ZurichGroupId, Name = "Zürich", Latitude = 47.376, Longitude = 8.541 },
            new() { Id = Guid.NewGuid(), Name = "Ohne Ort" },
            new() { Id = BernGroupId, Name = "Bern", Latitude = 46.948, Longitude = 7.447 }
        });
    }

    [TearDown]
    public void TearDown() => _api.Dispose();

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanEditClients", "CanViewGroups" },
        AccessToken = new BearerToken("caller-jwt")
    };

    private Task<SkillResult> RunAsync(string clientId, string? validFrom = "2026-05-01")
    {
        var parameters = new Dictionary<string, object> { ["clientId"] = clientId };
        if (validFrom != null)
        {
            parameters["validFrom"] = validFrom;
        }

        return _skill.ExecuteAsync(Ctx(), parameters);
    }

    private static JsonElement Data(SkillResult result) =>
        JsonDocument.Parse(JsonSerializer.Serialize(result.Data)).RootElement;

    private static List<string> PropertyNames(SkillResult result) =>
        Data(result).EnumerateObject().Select(p => p.Name).ToList();

    [Test]
    public async Task InvalidClientId_ReturnsExactText()
    {
        var result = await RunAsync("abc");

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe("Invalid client ID format: abc");
    }

    [Test]
    public async Task InvalidValidFrom_IsRejected_BeforeTheClientIsLoaded()
    {
        var result = await RunAsync(ClientId.ToString(), "irgendwann");

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(SkillDateParser.InvalidDateMessageFor("validFrom", "irgendwann"));
        await _clientRepository.DidNotReceive().Get(Arg.Any<Guid>());
    }

    [Test]
    public async Task UnknownClient_ReturnsExactText()
    {
        var unknownId = Guid.NewGuid();

        var result = await RunAsync(unknownId.ToString());

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe($"Client with ID {unknownId} not found.");
    }

    [Test]
    public async Task ClientWithoutGeocodedAddress_ChangesNothing_WithoutListingGroups()
    {
        _client.Addresses = new Collection<Address> { new() { Latitude = 46.9 } };

        var result = await RunAsync(ClientId.ToString());

        result.Success.ShouldBeTrue();
        result.Message.ShouldBe(
            "This client has no address with coordinates, so the nearest group could not be determined. Nothing was changed.");
        PropertyNames(result).ShouldBe(new[] { "ClientId", "Added", "Reason" });
        Data(result).GetProperty("Reason").GetString().ShouldBe("client has no geocoded address");
        await _groupRepository.DidNotReceive().List();
        _api.Calls.ShouldBeEmpty();
    }

    [Test]
    public async Task NoGeocodedGroup_ChangesNothing()
    {
        _groupRepository.List().Returns(new List<Group>
        {
            new() { Id = Guid.NewGuid(), Name = "Ohne Ort" },
            new() { Id = BernGroupId, Name = "Bern", Latitude = 46.948, Longitude = 7.447, IsDeleted = true }
        });

        var result = await RunAsync(ClientId.ToString());

        result.Success.ShouldBeTrue();
        result.Message.ShouldBe(
            "No group has a location resolved from its name (no geocoded groups), so the nearest group could not be determined. Nothing was changed.");
        PropertyNames(result).ShouldBe(new[] { "ClientId", "Added", "Reason" });
        Data(result).GetProperty("Reason").GetString().ShouldBe("no group carries coordinates derived from its name");
        _api.Calls.ShouldBeEmpty();
    }

    [Test]
    public async Task AlreadyInNearestGroup_WinsOverTheMissingStartDate()
    {
        _client.GroupItems.Add(new GroupItem { ClientId = ClientId, GroupId = BernGroupId });

        var result = await RunAsync(ClientId.ToString(), validFrom: null);

        result.Success.ShouldBeTrue();
        result.Message.ShouldBe("Client is already in the nearest group 'Bern'. Nothing was changed.");
        PropertyNames(result).ShouldBe(new[] { "ClientId", "GroupId", "GroupName", "Added", "DistanceKm", "Reason" });
        var data = Data(result);
        data.GetProperty("GroupId").GetGuid().ShouldBe(BernGroupId);
        data.GetProperty("DistanceKm").GetDouble().ShouldBe(0d);
        data.GetProperty("Reason").GetString().ShouldBe("client already in nearest group");
        _api.Calls.ShouldBeEmpty();
    }

    [Test]
    public async Task DeletedMembershipInNearestGroup_DoesNotCount()
    {
        _client.GroupItems.Add(new GroupItem { ClientId = ClientId, GroupId = BernGroupId, IsDeleted = true });

        var result = await RunAsync(ClientId.ToString());

        result.Success.ShouldBeTrue(result.Message);
        _api.SingleCall.Method.ShouldBe(HttpMethod.Post);
    }

    [Test]
    public async Task MissingStartDate_AsksForIt_NamingTheNearestGroup()
    {
        var result = await RunAsync(ClientId.ToString(), validFrom: null);

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(SkillDateParser.MissingStartDateMessage("add this client to the nearest group 'Bern'"));
        _api.Calls.ShouldBeEmpty();
    }

    [Test]
    public async Task Success_PostsTheMembership_AndReportsTheNearestGroup()
    {
        var result = await RunAsync(ClientId.ToString());

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldStartWith("Client added to the nearest group 'Bern' (0 km air-line). ");
        PropertyNames(result).ShouldBe(new[]
        {
            "GroupItemId", "ClientId", "GroupId", "GroupName", "DistanceKm", "Added", "MembershipStart"
        });
        var data = Data(result);
        data.GetProperty("GroupId").GetGuid().ShouldBe(BernGroupId);
        data.GetProperty("Added").GetBoolean().ShouldBeTrue();
        data.GetProperty("GroupItemId").GetGuid().ShouldNotBe(Guid.Empty);
        _api.SingleCall.Method.ShouldBe(HttpMethod.Post);
        var sent = _api.BodyOf<GroupItemResource>()!;
        sent.ClientId.ShouldBe(ClientId);
        sent.GroupId.ShouldBe(BernGroupId);
        sent.ValidFrom.ShouldBe(new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc));
        sent.ValidUntil.ShouldBeNull();
    }

    [Test]
    public async Task ApiFailure_IsPassedThroughVerbatim()
    {
        _api.RespondWithProblem(HttpMethod.Post, GroupItemsRoute, HttpStatusCode.Conflict, "boom");

        var result = await RunAsync(ClientId.ToString());

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe("boom");
    }
}
