// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Visibility tests for AssignContractToClientSkill: a client id the caller may not see by group visibility
/// is answered exactly like an unknown id, the client is never loaded and no update is sent.
/// </summary>

using Klacks.Api.Application.DTOs.Staffs;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Services.Assistant;
using Klacks.UnitTest.Infrastructure.SelfApi;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class AssignContractToClientSkillVisibilityTests
{
    private static readonly Guid ClientId = Guid.NewGuid();
    private static readonly Guid ContractId = Guid.NewGuid();

    private IClientRepository _clientRepository = null!;
    private IContractRepository _contractRepository = null!;
    private FakeSelfApi _api = null!;

    [SetUp]
    public void Setup()
    {
        _clientRepository = Substitute.For<IClientRepository>();
        _contractRepository = Substitute.For<IContractRepository>();
        _api = new FakeSelfApi();
        _api.Respond(HttpMethod.Put, "api/backend/Clients", new ClientResource());
        _clientRepository.Get(ClientId).Returns(new Client { Id = ClientId, FirstName = "Anna", Name = "Muster" });
        _contractRepository.Get(ContractId).Returns(new Contract { Id = ContractId, Name = "Full time" });
    }

    [TearDown]
    public void TearDown() => _api.Dispose();

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanEditClients" },
        AccessToken = new BearerToken("caller-jwt")
    };

    private static Dictionary<string, object> Params(Guid clientId) => new()
    {
        ["clientId"] = clientId.ToString(),
        ["contractId"] = ContractId.ToString(),
        ["fromDate"] = "2026-10-01"
    };

    private AssignContractToClientSkill Skill(IClientVisibilityGuard guard) => new(
        _clientRepository, guard, _contractRepository, new ClientMapper(), _api.Client, new SelfApiRouteResolver());

    [Test]
    public async Task HiddenClient_AnswersLikeUnknownId_WithoutLoadingOrUpdating()
    {
        var guard = SkillClientVisibility.Hiding(ClientId);
        var skill = Skill(guard);
        var unknownId = Guid.NewGuid();

        var hidden = await skill.ExecuteAsync(Ctx(), Params(ClientId));
        var unknown = await skill.ExecuteAsync(Ctx(), Params(unknownId));

        hidden.Success.ShouldBeFalse();
        unknown.Success.ShouldBeFalse();
        SkillClientVisibility.WithoutId(hidden.Message, ClientId)
            .ShouldBe(SkillClientVisibility.WithoutId(unknown.Message, unknownId));
        await guard.Received().IsVisibleAsync(ClientId, Arg.Any<CancellationToken>());
        await _clientRepository.DidNotReceive().Get(ClientId);
        _api.Calls.ShouldBeEmpty();
    }

    [Test]
    public async Task VisibleClient_GetsTheContract()
    {
        var result = await Skill(SkillClientVisibility.AllVisible()).ExecuteAsync(Ctx(), Params(ClientId));

        result.Success.ShouldBeTrue(result.Message);
        _api.SingleCall.Method.ShouldBe(HttpMethod.Put);
    }
}
