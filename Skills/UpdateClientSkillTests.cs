// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for UpdateClientSkill — id validation, partial updates, gender change side
/// effect on LegalEntity, no-op when nothing supplied. ClientRepository + UnitOfWork mocked.
/// </summary>

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Staffs;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class UpdateClientSkillTests
{
    private IClientRepository _clientRepository = null!;
    private IClientSearchRepository _searchRepository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private UpdateClientSkill _skill = null!;

    [SetUp]
    public void Setup()
    {
        _clientRepository = Substitute.For<IClientRepository>();
        _searchRepository = Substitute.For<IClientSearchRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _skill = new UpdateClientSkill(
            _clientRepository, _searchRepository, _unitOfWork, Substitute.For<ILogger<UpdateClientSkill>>());
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanEditClients" }
    };

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
        var parameters = new Dictionary<string, object> { ["clientId"] = id.ToString(), ["firstName"] = "Anna" };

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.True);
        await _clientRepository.Received(1).Put(Arg.Is<Client>(c => c.FirstName == "Anna" && c.Name == "Müller"));
    }

    [Test]
    public async Task SettingGenderToLegalEntity_AlsoSetsLegalEntityFlag()
    {
        var id = Guid.NewGuid();
        var existing = new Client { Id = id, FirstName = "Acme", Name = "GmbH", Gender = GenderEnum.Female, LegalEntity = false };
        _clientRepository.Get(id).Returns(existing);
        var parameters = new Dictionary<string, object>
        {
            ["clientId"] = id.ToString(),
            ["gender"] = "LegalEntity"
        };

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.True);
        await _clientRepository.Received(1).Put(Arg.Is<Client>(c =>
            c.Gender == GenderEnum.LegalEntity && c.LegalEntity));
    }
}
