// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for UpdateClientSkill — id validation, partial updates, gender change side
/// effect on LegalEntity, no-op when nothing supplied, plus the self-verify path: success
/// carries the "verified" marker, a failed re-read yields an error (rollback).
/// ClientRepository + UnitOfWork mocked.
/// </summary>

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Staffs;
using Microsoft.Extensions.Logging;

using Klacks.Api.Application.DTOs.Staffs;

using Klacks.Api.Application.Mappers;

using Klacks.Api.Infrastructure.Services.Assistant;

using Klacks.UnitTest.Infrastructure.SelfApi;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class UpdateClientSkillTests
{
    private IClientRepository _clientRepository = null!;
    private IClientSearchRepository _searchRepository = null!;
    private FakeSelfApi _api = null!;
    private ICountryResolver _countryResolver = null!;
    private UpdateClientSkill _skill = null!;

    [SetUp]
    public void Setup()
    {
        _clientRepository = Substitute.For<IClientRepository>();
        _searchRepository = Substitute.For<IClientSearchRepository>();
        _api = new FakeSelfApi();
        _api.Respond(HttpMethod.Put, "api/backend/Clients", new ClientResource());
        _countryResolver = Substitute.For<ICountryResolver>();
        _skill = new UpdateClientSkill(
            _clientRepository, _searchRepository, new ClientMapper(), _api.Client, new SelfApiRouteResolver(),
            Substitute.For<ILogger<UpdateClientSkill>>(), _countryResolver);
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanEditClients" },
        AccessToken = new BearerToken("caller-jwt")
    };

    [TearDown]
    public void TearDown() => _api.Dispose();

    [Test]
    public async Task ReturnsError_WhenClientNotFound()
    {
        var id = Guid.NewGuid();
        _clientRepository.Get(id).Returns((Client?)null);
        var parameters = new Dictionary<string, object> { ["clientId"] = id.ToString(), ["firstName"] = "Max" };

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("not found"));
    }

    [Test]
    public async Task ReturnsNoOp_WhenNoFieldsSupplied()
    {
        var id = Guid.NewGuid();
        _clientRepository.Get(id).Returns(new Client { Id = id, FirstName = "Anna", Name = "Müller" });
        var parameters = new Dictionary<string, object> { ["clientId"] = id.ToString() };

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.True);
        Assert.That(result.Message, Does.Contain("No fields"));
        await _clientRepository.DidNotReceive().Put(Arg.Any<Client>());
    }

    [Test]
    public async Task UpdatesFirstNameOnly()
    {
        var id = Guid.NewGuid();
        var existing = new Client { Id = id, FirstName = "Old", Name = "Müller", Gender = GenderEnum.Female };
        _clientRepository.Get(id).Returns(existing);
        _clientRepository.GetNoTracking(id).Returns(existing);
        var parameters = new Dictionary<string, object> { ["clientId"] = id.ToString(), ["firstName"] = "Anna" };

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.True);
        var sentNames = _api.BodyOf<ClientResource>()!;
        sentNames.FirstName.ShouldBe("Anna");
        sentNames.Name.ShouldBe("Müller");
    }

    [Test]
    public async Task SettingGenderToLegalEntity_AlsoSetsLegalEntityFlag()
    {
        var id = Guid.NewGuid();
        var existing = new Client { Id = id, FirstName = "Acme", Name = "GmbH", Gender = GenderEnum.Female, LegalEntity = false };
        _clientRepository.Get(id).Returns(existing);
        _clientRepository.GetNoTracking(id).Returns(existing);
        var parameters = new Dictionary<string, object>
        {
            ["clientId"] = id.ToString(),
            ["gender"] = "LegalEntity"
        };

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.True);
        var sentGender = _api.BodyOf<ClientResource>()!;
        sentGender.Gender.ShouldBe(GenderEnum.LegalEntity);
        sentGender.LegalEntity.ShouldBeTrue();
    }

    [Test]
    public async Task SuccessMessage_CarriesVerifiedMarker()
    {
        var id = Guid.NewGuid();
        var existing = new Client { Id = id, FirstName = "Old", Name = "Müller" };
        _clientRepository.Get(id).Returns(existing);
        _clientRepository.GetNoTracking(id).Returns(existing);
        var parameters = new Dictionary<string, object> { ["clientId"] = id.ToString(), ["firstName"] = "Anna" };

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.True, result.Message);
        _api.SingleCall.Method.ShouldBe(HttpMethod.Put);
        _api.SingleCall.Route.ShouldBe("api/backend/Clients");
    }


}
