// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for AssignContractByNameSkill, driven by the 2026-07-07 skill-usage failure
/// classes: a verbatim user sentence as contract name fails with a no-retry error and never
/// writes, a label-prefixed contract name ("Vertrag Teilzeit 0 Std BE") resolves and assigns,
/// an ambiguous contract name lists all candidates, and duplicate client names are resolvable
/// via the optional idNumber parameter.
/// </summary>

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Staffs;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class AssignContractByNameSkillTests
{
    private IClientRepository _clientRepository = null!;
    private IClientSearchRepository _searchRepository = null!;
    private IContractRepository _contractRepository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private AssignContractByNameSkill _skill = null!;

    private static readonly Guid ClientId = Guid.NewGuid();
    private static readonly Guid PartTimeContractId = Guid.NewGuid();
    private static readonly Guid FullTime160Id = Guid.NewGuid();
    private static readonly Guid FullTime180Id = Guid.NewGuid();

    [SetUp]
    public void Setup()
    {
        _clientRepository = Substitute.For<IClientRepository>();
        _searchRepository = Substitute.For<IClientSearchRepository>();
        _contractRepository = Substitute.For<IContractRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _skill = new AssignContractByNameSkill(
            _clientRepository, _searchRepository, _contractRepository, _unitOfWork);

        _searchRepository.SearchAsync(
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<EntityTypeEnum?>(),
                Arg.Any<Guid?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ClientSearchResult
            {
                Items = new List<ClientSearchItem>
                {
                    new() { Id = ClientId, FirstName = "Max", LastName = "Müller", IdNumber = 77 }
                },
                TotalCount = 1
            });
        _clientRepository.Get(ClientId).Returns(new Client { Id = ClientId, FirstName = "Max", Name = "Müller" });

        _contractRepository.List().Returns(new List<Contract>
        {
            new() { Id = FullTime160Id, Name = "Vollzeit 160 BE" },
            new() { Id = FullTime180Id, Name = "Vollzeit 180 BE" },
            new() { Id = PartTimeContractId, Name = "Teilzeit 0 Std BE" }
        });
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanEditClients" }
    };

    private static Dictionary<string, object> Parameters(string contractName) => new()
    {
        ["firstName"] = "Max",
        ["lastName"] = "Müller",
        ["contractName"] = contractName,
        ["fromDate"] = "2026-07-01"
    };

    [Test]
    public async Task VerbatimUserSentenceAsContractName_FailsWithNoRetryError_AndDoesNotWrite()
    {
        var result = await _skill.ExecuteAsync(
            Ctx(), Parameters("Keine Kontaktdaten, bitte ohne Adresse anlegen."));

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("No contract found matching"));
        Assert.That(result.Message, Does.Contain("Do not call this skill again with the same value"));
        Assert.That(result.Message, Does.Contain("Vollzeit 160 BE"));
        await _clientRepository.DidNotReceive().Put(Arg.Any<Client>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task LabelPrefixedContractName_ResolvesAndAssignsContract()
    {
        var result = await _skill.ExecuteAsync(Ctx(), Parameters("Vertrag Teilzeit 0 Std BE"));

        Assert.That(result.Success, Is.True);
        await _clientRepository.Received(1).Put(Arg.Is<Client>(c =>
            c.ClientContracts.Any(cc => cc.ContractId == PartTimeContractId && cc.IsActive)));
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task AmbiguousContractName_ListsAllCandidates_AndDoesNotWrite()
    {
        var result = await _skill.ExecuteAsync(Ctx(), Parameters("Vollzeit"));

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("Multiple contracts match"));
        Assert.That(result.Message, Does.Contain("Vollzeit 160 BE"));
        Assert.That(result.Message, Does.Contain("Vollzeit 180 BE"));
        await _clientRepository.DidNotReceive().Put(Arg.Any<Client>());
    }

    [Test]
    public async Task DuplicateClients_WithIdNumberParameter_AssignsToThatClient()
    {
        var otherId = Guid.NewGuid();
        _searchRepository.SearchAsync(
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<EntityTypeEnum?>(),
                Arg.Any<Guid?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ClientSearchResult
            {
                Items = new List<ClientSearchItem>
                {
                    new() { Id = otherId, FirstName = "Max", LastName = "Müller", IdNumber = 77 },
                    new() { Id = ClientId, FirstName = "Max", LastName = "Müller", IdNumber = 78 }
                },
                TotalCount = 2
            });

        var parameters = Parameters("Teilzeit 0 Std BE");
        parameters[ClientResolver.IdNumberParameterName] = 78;

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.True);
        await _clientRepository.Received(1).Get(ClientId);
        await _clientRepository.Received(1).Put(Arg.Is<Client>(c => c.Id == ClientId));
    }

    [Test]
    public async Task DuplicateClients_WithoutIdNumber_ErrorNamesTheIdNumberParameter()
    {
        _searchRepository.SearchAsync(
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<EntityTypeEnum?>(),
                Arg.Any<Guid?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ClientSearchResult
            {
                Items = new List<ClientSearchItem>
                {
                    new() { Id = Guid.NewGuid(), FirstName = "Max", LastName = "Müller", IdNumber = 77 },
                    new() { Id = Guid.NewGuid(), FirstName = "Max", LastName = "Müller", IdNumber = 78 }
                },
                TotalCount = 2
            });

        var result = await _skill.ExecuteAsync(Ctx(), Parameters("Teilzeit 0 Std BE"));

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain(ClientResolver.IdNumberParameterName));
        Assert.That(result.Message, Does.Contain("#77"));
        Assert.That(result.Message, Does.Contain("#78"));
        await _clientRepository.DidNotReceive().Put(Arg.Any<Client>());
    }
}
