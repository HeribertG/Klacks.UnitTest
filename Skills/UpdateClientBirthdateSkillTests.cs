// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for UpdateClientBirthdateSkill — invalid date, unresolved / ambiguous client,
/// verified happy path (message carries "verified") and the rollback path when the re-read
/// does not confirm the new birthdate.
/// </summary>

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Staffs;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class UpdateClientBirthdateSkillTests
{
    private IClientRepository _clientRepository = null!;
    private IClientSearchRepository _searchRepository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private UpdateClientBirthdateSkill _skill = null!;

    [SetUp]
    public void Setup()
    {
        _clientRepository = Substitute.For<IClientRepository>();
        _searchRepository = Substitute.For<IClientSearchRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<bool>>>())
            .Returns(ci => ci.Arg<Func<Task<bool>>>()());
        _skill = new UpdateClientBirthdateSkill(_clientRepository, _searchRepository, _unitOfWork);
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
        var client = new Client { Id = Guid.NewGuid(), FirstName = firstName, Name = lastName };
        WireSearch(new ClientSearchItem { Id = client.Id, FirstName = firstName, LastName = lastName, IdNumber = 7 });
        _clientRepository.Get(client.Id).Returns(client);
        _clientRepository.GetNoTracking(client.Id).Returns(client);
        return client;
    }

    private static Dictionary<string, object> Parameters(string birthdate = "1990-04-12") => new()
    {
        ["firstName"] = "Anna",
        ["lastName"] = "Müller",
        ["birthdate"] = birthdate
    };

    [Test]
    public async Task ReturnsError_WhenBirthdateInvalid()
    {
        var result = await _skill.ExecuteAsync(Ctx(), Parameters(birthdate: "not-a-date"));

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("Invalid birthdate"));
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
    public async Task ReturnsError_WhenMultipleClientsMatch()
    {
        WireSearch(
            new ClientSearchItem { Id = Guid.NewGuid(), FirstName = "Anna", LastName = "Müller", IdNumber = 1 },
            new ClientSearchItem { Id = Guid.NewGuid(), FirstName = "Anna", LastName = "Müller", IdNumber = 2 });

        var result = await _skill.ExecuteAsync(Ctx(), Parameters());

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("Multiple clients match"));
        await _clientRepository.DidNotReceive().Put(Arg.Any<Client>());
    }

    [Test]
    public async Task UpdatesBirthdate_AndConfirmsInDatabase()
    {
        var client = WireResolvedClient();

        var result = await _skill.ExecuteAsync(Ctx(), Parameters());

        Assert.That(result.Success, Is.True, result.Message);
        Assert.That(result.Message, Does.Contain("verified"));
        Assert.That(client.Birthdate, Is.EqualTo(new DateTime(1990, 4, 12)));
        await _clientRepository.Received(1).Put(client);
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task ReturnsError_WhenVerificationRereadIsStale()
    {
        var client = WireResolvedClient();
        _clientRepository.GetNoTracking(client.Id)
            .Returns(new Client { Id = client.Id, FirstName = "Anna", Name = "Müller", Birthdate = new DateTime(1980, 1, 1) });

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
