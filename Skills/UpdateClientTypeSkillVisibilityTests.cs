// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Visibility tests for UpdateClientTypeSkill: a client id the caller may not see by group visibility is
/// answered exactly like an unknown id, the client is never loaded and no update is sent.
/// </summary>

using Klacks.Api.Application.DTOs.Staffs;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Services.Assistant;
using Klacks.UnitTest.Infrastructure.SelfApi;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class UpdateClientTypeSkillVisibilityTests
{
    private static readonly Guid ClientId = Guid.NewGuid();

    private IClientRepository _clientRepository = null!;
    private IClientSearchRepository _searchRepository = null!;
    private FakeSelfApi _api = null!;

    [SetUp]
    public void Setup()
    {
        _clientRepository = Substitute.For<IClientRepository>();
        _searchRepository = Substitute.For<IClientSearchRepository>();
        _api = new FakeSelfApi();
        _api.Respond(HttpMethod.Put, "api/backend/Clients", new ClientResource());
        _clientRepository.Get(ClientId).Returns(new Client
        {
            Id = ClientId,
            FirstName = "Anna",
            Name = "Muster",
            Type = EntityTypeEnum.Employee
        });
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
        ["targetType"] = "Customer"
    };

    [Test]
    public async Task HiddenClient_AnswersLikeUnknownId_WithoutLoadingOrUpdating()
    {
        var guard = SkillClientVisibility.Hiding(ClientId);
        var skill = new UpdateClientTypeSkill(
            _clientRepository, guard, _searchRepository, new ClientMapper(), _api.Client, new SelfApiRouteResolver(),
            Substitute.For<ILogger<UpdateClientTypeSkill>>());
        var unknownId = Guid.NewGuid();

        var hidden = await skill.ExecuteAsync(Ctx(), Params(ClientId));
        var unknown = await skill.ExecuteAsync(Ctx(), Params(unknownId));

        hidden.Success.ShouldBeFalse();
        unknown.Success.ShouldBeFalse();
        SkillClientVisibility.WithoutId(hidden.Message, ClientId)
            .ShouldBe(SkillClientVisibility.WithoutId(unknown.Message, unknownId));
        await guard.Received().IsVisibleAsync(ClientId, Arg.Any<CancellationToken>());
        await _clientRepository.DidNotReceive().Get(ClientId);
        await _searchRepository.DidNotReceiveWithAnyArgs().SearchAsync(default!, default, default, default, default, default);
        _api.Calls.ShouldBeEmpty();
    }

    [Test]
    public async Task VisibleClient_IsUpdated()
    {
        var skill = new UpdateClientTypeSkill(
            _clientRepository, SkillClientVisibility.AllVisible(), _searchRepository, new ClientMapper(), _api.Client,
            new SelfApiRouteResolver(), Substitute.For<ILogger<UpdateClientTypeSkill>>());

        var result = await skill.ExecuteAsync(Ctx(), Params(ClientId));

        result.Success.ShouldBeTrue(result.Message);
        _api.SingleCall.Method.ShouldBe(HttpMethod.Put);
    }
}
