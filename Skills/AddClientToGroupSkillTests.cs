// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for AddClientToGroupSkill: the membership is written inside a transaction and re-read from
/// the database; a confirmed read reports a verified success, a missing read rolls the write back and
/// reports an error instead of a false success, and an existing membership is rejected up front.
/// </summary>

using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Associations;

using Klacks.Api.Application.DTOs.Associations;

using Klacks.Api.Infrastructure.Services.Assistant;

using Klacks.UnitTest.Infrastructure.SelfApi;
using Klacks.UnitTest.TestHelpers;
using System.Text.Json;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class AddClientToGroupSkillTests
{
    private IClientRepository _clientRepository = null!;
    private IGroupRepository _groupRepository = null!;
    private IGroupItemRepository _groupItemRepository = null!;
    private FakeSelfApi _api = null!;
    private ICompanyClock _companyClock = null!;
    private AddClientToGroupSkill _skill = null!;

    private static readonly Guid ClientId = Guid.NewGuid();
    private static readonly Guid GroupId = Guid.NewGuid();

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
        _skill = new AddClientToGroupSkill(
            _clientRepository, SkillClientVisibility.AllVisible(), _groupRepository, TestGroupScopeGuard.Unrestricted(), _groupItemRepository, _api.Client, new SelfApiRouteResolver(), _companyClock, TestGroupMembershipStart.Planner(_companyClock));

        _clientRepository.Exists(ClientId).Returns(true);
        _groupRepository.Get(GroupId).Returns(new Group { Id = GroupId, Name = "Bern" });
        _groupItemRepository.GetByClientAndGroup(ClientId, GroupId).Returns((GroupItem?)null);

    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanEditClients", "CanViewGroups" },
        AccessToken = new BearerToken("caller-jwt")
    };

    private Dictionary<string, object> Params() => new()
    {
        ["clientId"] = ClientId.ToString(),
        ["groupId"] = GroupId.ToString(),
        ["validFrom"] = "2026-05-01"
    };

    [TearDown]
    public void TearDown() => _api.Dispose();

    [Test]
    public async Task Adds_AndReportsVerified_WhenPersistenceIsConfirmed()
    {
        _groupItemRepository.GetNoTracking(Arg.Any<Guid>())
            .Returns(ci => new GroupItem { Id = ci.Arg<Guid>(), ClientId = ClientId, GroupId = GroupId });

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        Assert.That(result.Success, Is.True);
        _api.SingleCall.Method.ShouldBe(HttpMethod.Post);
    }


    [Test]
    public async Task Rejects_WhenClientIsAlreadyAMember()
    {
        _groupItemRepository.GetByClientAndGroup(ClientId, GroupId)
            .Returns(new GroupItem { Id = Guid.NewGuid(), ClientId = ClientId, GroupId = GroupId });

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("already a member"));
        _api.Calls.ShouldBeEmpty();
    }

    [Test]
    public async Task AsksForStartDate_AndDoesNotPersist_WhenValidFromIsMissing()
    {
        var parameters = new Dictionary<string, object>
        {
            ["clientId"] = ClientId.ToString(),
            ["groupId"] = GroupId.ToString()
        };

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("date"));
        _api.Calls.ShouldBeEmpty();
    }

    [Test]
    public async Task HiddenClient_AnswersLikeUnknownId_WithoutPosting()
    {
        var guard = SkillClientVisibility.Hiding(ClientId);
        var skill = new AddClientToGroupSkill(
            _clientRepository, guard, _groupRepository, TestGroupScopeGuard.Unrestricted(), _groupItemRepository, _api.Client, new SelfApiRouteResolver(), _companyClock, TestGroupMembershipStart.Planner(_companyClock));
        var unknownId = Guid.NewGuid();
        var unknownParams = Params();
        unknownParams["clientId"] = unknownId.ToString();

        var hidden = await skill.ExecuteAsync(Ctx(), Params());
        var unknown = await skill.ExecuteAsync(Ctx(), unknownParams);

        Assert.That(hidden.Success, Is.False);
        Assert.That(unknown.Success, Is.False);
        Assert.That(SkillClientVisibility.WithoutId(hidden.Message, ClientId),
            Is.EqualTo(SkillClientVisibility.WithoutId(unknown.Message, unknownId)));
        await guard.Received().IsVisibleAsync(ClientId, Arg.Any<CancellationToken>());
        await _groupItemRepository.DidNotReceive().GetByClientAndGroup(ClientId, Arg.Any<Guid>());
        _api.Calls.ShouldBeEmpty();
    }

    private AddClientToGroupSkill SkillWithEntryDate() => new(
        _clientRepository, SkillClientVisibility.AllVisible(), _groupRepository, TestGroupScopeGuard.Unrestricted(),
        _groupItemRepository, _api.Client, new SelfApiRouteResolver(), _companyClock,
        TestGroupMembershipStart.Planner(_companyClock, (ClientId, new DateOnly(2021, 4, 1))));

    [Test]
    public async Task EntryKeyword_StartsThePersonOnTheirEntryDate_AndSaysSo()
    {
        var parameters = Params();
        parameters["validFrom"] = "entry";

        var result = await SkillWithEntryDate().ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.True);
        _api.BodyOf<GroupItemResource>()!.ValidFrom.ShouldBe(new DateTime(2021, 4, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.That(result.Message, Does.Contain("each person's own employment entry date"));
    }

    [Test]
    public async Task AGivenDateAfterTheEntry_NamesTheBlueLockedConsequence()
    {
        var result = await SkillWithEntryDate().ExecuteAsync(Ctx(), Params());

        Assert.That(result.Success, Is.True);
        Assert.That(result.Message, Does.Contain("blue and locked"));
        Assert.That(result.Message, Does.Contain("2021-04-01"));
        JsonSerializer.Serialize(result.Data).ShouldContain("\"PersonsEnteredBeforeStartCount\":1");
    }

    [Test]
    public async Task AGivenDateWithoutAnEarlierEntry_AddsNoHint()
    {
        var result = await _skill.ExecuteAsync(Ctx(), Params());

        Assert.That(result.Message, Does.Not.Contain("blue and locked"));
    }

    [Test]
    public async Task ValidUntilBeforeTheStart_IsRefused_AndNothingIsPosted()
    {
        var parameters = Params();
        parameters["validUntil"] = "2026-04-01";

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("validUntil"));
        _api.Calls.ShouldBeEmpty();
    }

    [Test]
    public async Task ValidUntilBeforeThePersonsEntryDate_InEntryMode_IsRefused()
    {
        var parameters = Params();
        parameters["validFrom"] = "entry";
        parameters["validUntil"] = "2021-03-01";

        var result = await SkillWithEntryDate().ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("validUntil"));
        _api.Calls.ShouldBeEmpty();
    }

    [Test]
    public async Task ValidUntilOnTheStartDay_IsAccepted()
    {
        var parameters = Params();
        parameters["validUntil"] = "2026-05-01";

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.True);
    }
}
