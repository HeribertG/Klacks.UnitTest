// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for UpdateClientGenderSkill — unknown gender value, unresolved client,
/// verified happy path (message carries "verified") and the rollback path when the re-read
/// does not confirm the new gender.
/// </summary>

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Staffs;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class UpdateClientGenderSkillTests
{
    private IClientRepository _clientRepository = null!;
    private IClientSearchRepository _searchRepository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private UpdateClientGenderSkill _skill = null!;

    [SetUp]
    public void Setup()
    {
        _clientRepository = Substitute.For<IClientRepository>();
        _searchRepository = Substitute.For<IClientSearchRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<bool>>>())
            .Returns(ci => ci.Arg<Func<Task<bool>>>()());
        _skill = new UpdateClientGenderSkill(_clientRepository, _searchRepository, _unitOfWork);
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanEditClients" }
    };

    private void WireSearch(params ClientSearchItem[] items)
    {
        _searchRepository.SearchAsync(
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<EntityTypeEnum?>(),
                Arg.Any<Guid?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ClientSearchResult { Items = items, TotalCount = items.Length });
    }

    private Client WireResolvedClient(string firstName = "Anna", string lastName = "Müller")
    {
        var client = new Client { Id = Guid.NewGuid(), FirstName = firstName, Name = lastName, Gender = GenderEnum.Male };
        WireSearch(new ClientSearchItem { Id = client.Id, FirstName = firstName, LastName = lastName, IdNumber = 7 });
        _clientRepository.Get(client.Id).Returns(client);
        _clientRepository.GetNoTracking(client.Id).Returns(client);
        return client;
    }

    private static Dictionary<string, object> Parameters(string gender = "Female") => new()
    {
        ["firstName"] = "Anna",
        ["lastName"] = "Müller",
        ["gender"] = gender
    };

    [Test]
    public async Task ReturnsError_WhenGenderUnknown()
    {
        var result = await _skill.ExecuteAsync(Ctx(), Parameters(gender: "dragon"));

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("Unknown gender value"));
        await _clientRepository.DidNotReceive().Put(Arg.Any<Client>());
    }

    [Test]
    public async Task ReturnsError_WhenClientNotFound()
    {
        WireSearch();

        var result = await _skill.ExecuteAsync(Ctx(), Parameters());

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("No client found"));
        await _clientRepository.DidNotReceive().Put(Arg.Any<Client>());
    }

    [Test]
    public async Task UpdatesGender_AndConfirmsInDatabase()
    {
        var client = WireResolvedClient();

        var result = await _skill.ExecuteAsync(Ctx(), Parameters());

        Assert.That(result.Success, Is.True, result.Message);
        Assert.That(result.Message, Does.Contain("verified"));
        Assert.That(client.Gender, Is.EqualTo(GenderEnum.Female));
        await _clientRepository.Received(1).Put(client);
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task ReturnsError_WhenVerificationRereadIsStale()
    {
        var client = WireResolvedClient();
        _clientRepository.GetNoTracking(client.Id)
            .Returns(new Client { Id = client.Id, FirstName = "Anna", Name = "Müller", Gender = GenderEnum.Male });

        var result = await _skill.ExecuteAsync(Ctx(), Parameters());

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("verification failed"));
    }

    [Test]
    public async Task ReturnsError_WhenVerificationRereadIsMissing()
    {
        var client = WireResolvedClient();
        _clientRepository.GetNoTracking(client.Id).Returns((Client?)null);

        var result = await _skill.ExecuteAsync(Ctx(), Parameters());

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("verification failed"));
    }
}
