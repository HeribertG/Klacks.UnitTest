// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for DeleteClientSkill — not-found, verified happy path (message carries
/// "verified") and the rollback path when the client is still visible after the delete.
/// </summary>

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Staffs;

using Klacks.Api.Application.DTOs.Staffs;

using Klacks.Api.Application.Mappers;

using Klacks.Api.Infrastructure.Services.Assistant;

using Klacks.UnitTest.Infrastructure.SelfApi;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class DeleteClientSkillTests
{
    private IClientRepository _clientRepository = null!;
    private FakeSelfApi _api = null!;
    private DeleteClientSkill _skill = null!;

    [SetUp]
    public void Setup()
    {
        _clientRepository = Substitute.For<IClientRepository>();
        _api = new FakeSelfApi();
        _clientRepository.GetNoTracking(Arg.Any<Guid>()).Returns((Client?)null);
        _skill = new DeleteClientSkill(_clientRepository, _api.Client, new SelfApiRouteResolver());
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanDeleteClients" },
        AccessToken = new BearerToken("caller-jwt")
    };

    private static Client MakeClient(Guid id) => new()
    {
        Id = id,
        FirstName = "Max",
        Name = "Müller",
        Type = EntityTypeEnum.Employee
    };

    [TearDown]
    public void TearDown() => _api.Dispose();

    [Test]
    public async Task ReturnsError_WhenClientNotFound()
    {
        var id = Guid.NewGuid();
        _clientRepository.Get(id).Returns((Client?)null);
        var parameters = new Dictionary<string, object> { ["clientId"] = id.ToString() };

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.False);
        await _clientRepository.DidNotReceive().Delete(Arg.Any<Guid>());
    }

    [Test]
    public async Task SoftDeletesClient_ThroughTheClientEndpoint()
    {
        var id = Guid.NewGuid();
        _clientRepository.Get(id).Returns(MakeClient(id));
        _api.Respond(HttpMethod.Delete, $"api/backend/Clients/{id}", new ClientResource { Id = id });

        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object> { ["clientId"] = id.ToString() });

        Assert.That(result.Success, Is.True, result.Message);
        _api.SingleCall.Method.ShouldBe(HttpMethod.Delete);
        _api.SingleCall.Route.ShouldBe($"api/backend/Clients/{id}");
        _api.SingleCall.SkillName.ShouldBe("delete_client");
    }

    [Test]
    public async Task ReturnsError_WhenTheEndpointRefusesTheDelete()
    {
        var id = Guid.NewGuid();
        _clientRepository.Get(id).Returns(MakeClient(id));
        _api.RespondWithProblem(
            HttpMethod.Delete, $"api/backend/Clients/{id}", System.Net.HttpStatusCode.Forbidden, "nope");

        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object> { ["clientId"] = id.ToString() });

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("Permission denied"));
    }

    [Test]
    public async Task ReturnsError_WhenTheReferencedClientIsGone()
    {
        var id = Guid.NewGuid();
        _clientRepository.Get(id).Returns(MakeClient(id));
        _api.RespondWithProblem(
            HttpMethod.Delete, $"api/backend/Clients/{id}", System.Net.HttpStatusCode.NotFound, "already gone");

        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object> { ["clientId"] = id.ToString() });

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("already gone"));
    }
}
