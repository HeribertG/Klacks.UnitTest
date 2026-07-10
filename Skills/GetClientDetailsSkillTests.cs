// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the read-only GetClientDetailsSkill — invalid id format, client not found,
/// happy path with contract/group/address counts and filtering of soft-deleted children.
/// </summary>

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Staffs;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class GetClientDetailsSkillTests
{
    private IClientRepository _clientRepository = null!;
    private GetClientDetailsSkill _skill = null!;

    [SetUp]
    public void Setup()
    {
        _clientRepository = Substitute.For<IClientRepository>();
        _skill = new GetClientDetailsSkill(_clientRepository);
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanViewClients" }
    };

    private static Client MakeClientWithChildren(Guid id)
    {
        var client = new Client
        {
            Id = id,
            FirstName = "Anna",
            Name = "Müller",
            Gender = GenderEnum.Female,
            Type = EntityTypeEnum.Employee
        };

        client.ClientContracts.Add(new ClientContract
        {
            Id = Guid.NewGuid(),
            ClientId = id,
            ContractId = Guid.NewGuid(),
            Contract = new Contract { Name = "Standard", GuaranteedHours = 80m },
            FromDate = new DateOnly(2026, 1, 1),
            IsActive = true
        });
        client.ClientContracts.Add(new ClientContract
        {
            Id = Guid.NewGuid(),
            ClientId = id,
            ContractId = Guid.NewGuid(),
            Contract = new Contract { Name = "Old" },
            FromDate = new DateOnly(2020, 1, 1),
            IsDeleted = true
        });

        client.GroupItems.Add(new GroupItem
        {
            Id = Guid.NewGuid(),
            ClientId = id,
            GroupId = Guid.NewGuid(),
            Group = new Group { Name = "Team Bern" }
        });

        client.Addresses.Add(new Address
        {
            Id = Guid.NewGuid(),
            ClientId = id,
            Street = "Bahnhofstrasse 1",
            Zip = "3011",
            City = "Bern",
            State = "BE",
            Country = "CH",
            Type = AddressTypeEnum.Employee
        });

        client.Communications.Add(new Communication
        {
            Id = Guid.NewGuid(),
            ClientId = id,
            Type = CommunicationTypeEnum.PrivateMail,
            Value = "anna@example.com"
        });

        return client;
    }

    [Test]
    public async Task ReturnsError_WhenClientIdIsNotAGuid()
    {
        var result = await _skill.ExecuteAsync(
            Ctx(), new Dictionary<string, object> { ["clientId"] = "not-a-guid" });

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("Invalid client ID format"));
        await _clientRepository.DidNotReceive().Get(Arg.Any<Guid>());
    }

    [Test]
    public async Task ReturnsError_WhenClientNotFound()
    {
        var id = Guid.NewGuid();
        _clientRepository.Get(id).Returns((Client?)null);

        var result = await _skill.ExecuteAsync(
            Ctx(), new Dictionary<string, object> { ["clientId"] = id.ToString() });

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("not found"));
    }

    [Test]
    public async Task ReturnsDetails_WithCounts_AndFiltersSoftDeletedChildren()
    {
        var id = Guid.NewGuid();
        _clientRepository.Get(id).Returns(MakeClientWithChildren(id));

        var result = await _skill.ExecuteAsync(
            Ctx(), new Dictionary<string, object> { ["clientId"] = id.ToString() });

        Assert.That(result.Success, Is.True, result.Message);
        Assert.That(result.Message, Does.Contain("Anna Müller"));
        Assert.That(result.Message, Does.Contain("1 contract(s)"));
        Assert.That(result.Message, Does.Contain("1 group(s)"));
        Assert.That(result.Data, Is.Not.Null);
    }

    [Test]
    public async Task ReturnsDetails_ForClientWithoutChildren()
    {
        var id = Guid.NewGuid();
        _clientRepository.Get(id).Returns(new Client
        {
            Id = id,
            FirstName = "Max",
            Name = "Meier",
            Type = EntityTypeEnum.Employee
        });

        var result = await _skill.ExecuteAsync(
            Ctx(), new Dictionary<string, object> { ["clientId"] = id.ToString() });

        Assert.That(result.Success, Is.True, result.Message);
        Assert.That(result.Message, Does.Contain("0 contract(s)"));
        Assert.That(result.Message, Does.Contain("0 group(s)"));
    }
}
