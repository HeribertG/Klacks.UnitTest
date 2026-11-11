// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Whose roster order a Klacksy skill hands to a wizard start: the chat user's saved order (ranked first, unranked
/// employees after them in their incoming order, UserDefined true), the automatic order when the user saved none or
/// none of the run's employees, and always the automatic order for another AI (MCP) and an unattended self-reflection
/// plan, even when the token owner saved an order.
/// </summary>

using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.Constants;

namespace Klacks.UnitTest.Application.Services.Schedules;

[TestFixture]
public class SkillRosterOrderResolverTests
{
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _groupId = Guid.NewGuid();
    private readonly Guid _first = Guid.NewGuid();
    private readonly Guid _second = Guid.NewGuid();
    private readonly Guid _third = Guid.NewGuid();
    private readonly Guid _unranked = Guid.NewGuid();

    private IClientSortPreferenceRepository _repository = null!;
    private SkillRosterOrderResolver _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _repository = Substitute.For<IClientSortPreferenceRepository>();
        _repository.GetByUserAndGroupAsync(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new List<ClientSortPreference>());
        _sut = new SkillRosterOrderResolver(_repository);
    }

    [Test]
    public async Task ChatUserWithSavedOrder_GetsThatOrderAndUserDefined()
    {
        StubSavedOrder(_third, _second, _first);

        var (agentIds, userDefined) = await _sut.ResolveAsync(
            ChatContext(), _groupId, [_first, _second, _third], CancellationToken.None);

        agentIds.ShouldBe(new[] { _third, _second, _first });
        userDefined.ShouldBeTrue();
    }

    [Test]
    public async Task ChatUserWithSavedOrder_AppendsUnrankedEmployeesInTheirIncomingOrder()
    {
        StubSavedOrder(_second, _first);

        var (agentIds, userDefined) = await _sut.ResolveAsync(
            ChatContext(), _groupId, [_unranked, _first, _third, _second], CancellationToken.None);

        agentIds.ShouldBe(new[] { _second, _first, _unranked, _third });
        userDefined.ShouldBeTrue();
    }

    [Test]
    public async Task ChatUserWithoutSavedOrder_KeepsTheIncomingOrderAndAutomatic()
    {
        var (agentIds, userDefined) = await _sut.ResolveAsync(
            ChatContext(), _groupId, [_first, _second], CancellationToken.None);

        agentIds.ShouldBe(new[] { _first, _second });
        userDefined.ShouldBeFalse();
    }

    [Test]
    public async Task SavedOrderNamingNoEmployeeOfTheRun_IsAutomatic()
    {
        StubSavedOrder(Guid.NewGuid(), Guid.NewGuid());

        var (agentIds, userDefined) = await _sut.ResolveAsync(
            ChatContext(), _groupId, [_first, _second], CancellationToken.None);

        agentIds.ShouldBe(new[] { _first, _second });
        userDefined.ShouldBeFalse();
    }

    [TestCase(PersonalAccessTokenAccessMode.Read)]
    [TestCase(PersonalAccessTokenAccessMode.Write)]
    public async Task ExternalAgent_IsAutomaticEvenWithASavedOrder(PersonalAccessTokenAccessMode accessMode)
    {
        StubSavedOrder(_second, _first);

        var (agentIds, userDefined) = await _sut.ResolveAsync(
            ChatContext() with { ExternalAgentAccessMode = accessMode }, _groupId, [_first, _second], CancellationToken.None);

        agentIds.ShouldBe(new[] { _first, _second });
        userDefined.ShouldBeFalse();
    }

    [Test]
    public async Task SelfReflectionPlan_IsAutomaticEvenWithASavedOrder()
    {
        StubSavedOrder(_second, _first);
        var context = ChatContext() with { SessionId = GoalSelfReflectionAuditConstants.SessionIdPrefix + Guid.NewGuid() };

        var (agentIds, userDefined) = await _sut.ResolveAsync(context, _groupId, [_first, _second], CancellationToken.None);

        agentIds.ShouldBe(new[] { _first, _second });
        userDefined.ShouldBeFalse();
    }

    private void StubSavedOrder(params Guid[] clientIds)
    {
        _repository.GetByUserAndGroupAsync(_userId.ToString(), _groupId, Arg.Any<CancellationToken>())
            .Returns(clientIds
                .Select((clientId, index) => new ClientSortPreference
                {
                    UserId = _userId.ToString(),
                    GroupId = _groupId,
                    ClientId = clientId,
                    SortOrder = index,
                })
                .ToList());
    }

    private SkillExecutionContext ChatContext() => new()
    {
        UserId = _userId,
        TenantId = Guid.NewGuid(),
        UserName = "planner",
        UserPermissions = [],
        SessionId = Guid.NewGuid().ToString(),
    };
}
