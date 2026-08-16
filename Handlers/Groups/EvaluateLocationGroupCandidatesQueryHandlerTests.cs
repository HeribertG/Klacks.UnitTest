// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for EvaluateLocationGroupCandidatesQueryHandler: city bucket counts follow the same
/// preferred-address and name-uniqueness rules as CustomerGroupingPlanner, cities that already match an
/// existing group are excluded from candidates instead of proposing a duplicate name, and clients
/// without a usable address are reported separately instead of silently dropped.
/// </summary>

using Klacks.Api.Application.Handlers.Groups;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Queries.Groups;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Staffs;

namespace Klacks.UnitTest.Handlers.Groups;

[TestFixture]
public class EvaluateLocationGroupCandidatesQueryHandlerTests
{
    private IClientRepository _clientRepository = null!;
    private IGroupRepository _groupRepository = null!;
    private EvaluateLocationGroupCandidatesQueryHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        _clientRepository = Substitute.For<IClientRepository>();
        _groupRepository = Substitute.For<IGroupRepository>();
        _handler = new EvaluateLocationGroupCandidatesQueryHandler(_clientRepository, _groupRepository);

        _groupRepository.List().Returns(new List<Group>());
    }

    private static Client ClientWithCity(string city, AddressTypeEnum type = AddressTypeEnum.Employee) => new()
    {
        Id = Guid.NewGuid(),
        FirstName = "Test",
        Name = "Client",
        Type = EntityTypeEnum.Employee,
        Addresses = new List<Address>
        {
            new() { City = city, Type = type, ValidFrom = new DateTime(2026, 1, 1) }
        }
    };

    private static Client ClientWithoutAddress() => new()
    {
        Id = Guid.NewGuid(),
        FirstName = "No",
        Name = "Address",
        Type = EntityTypeEnum.Employee,
        Addresses = new List<Address>()
    };

    private void SetClients(params Client[] clients)
    {
        _clientRepository.GetByTypeWithAddressesAndGroupItemsAsync(Arg.Any<EntityTypeEnum>(), Arg.Any<CancellationToken>())
            .Returns(clients.ToList());
    }

    private static EvaluateLocationGroupCandidatesQuery Query() => new(EntityTypeEnum.Employee);

    [Test]
    public async Task CityAtOrAboveThreshold_IsListedAsCandidate()
    {
        SetClients(ClientWithCity("Winterthur"), ClientWithCity("Winterthur"), ClientWithCity("Winterthur"));

        var result = await _handler.Handle(Query(), CancellationToken.None);

        Assert.That(result.Candidates, Has.Count.EqualTo(1));
        Assert.That(result.Candidates[0].City, Is.EqualTo("Winterthur"));
        Assert.That(result.Candidates[0].ClientCount, Is.EqualTo(3));
        Assert.That(result.Candidates[0].IsViable, Is.True);
    }

    [Test]
    public async Task CityBelowThreshold_IsListedAsNearThreshold_NotAsCandidate()
    {
        SetClients(ClientWithCity("Wetzikon"), ClientWithCity("Wetzikon"));

        var result = await _handler.Handle(Query(), CancellationToken.None);

        Assert.That(result.Candidates, Is.Empty);
        Assert.That(result.NearThresholdCandidates, Has.Count.EqualTo(1));
        Assert.That(result.NearThresholdCandidates[0].City, Is.EqualTo("Wetzikon"));
        Assert.That(result.NearThresholdCandidates[0].IsViable, Is.False);
    }

    [Test]
    public async Task CityMatchingExistingUniqueGroupName_IsExcludedFromCandidates()
    {
        _groupRepository.List().Returns(new List<Group> { new() { Name = "Bern" } });
        SetClients(ClientWithCity("Bern"), ClientWithCity("Bern"), ClientWithCity("Bern"));

        var result = await _handler.Handle(Query(), CancellationToken.None);

        Assert.That(result.Candidates, Is.Empty);
        Assert.That(result.NearThresholdCandidates, Is.Empty);
        Assert.That(result.ClientsInExistingLocationGroup, Is.EqualTo(3));
    }

    [Test]
    public async Task ClientsWithoutUsableAddress_AreCountedSeparately_NotSilentlyDropped()
    {
        SetClients(ClientWithoutAddress(), ClientWithoutAddress());

        var result = await _handler.Handle(Query(), CancellationToken.None);

        Assert.That(result.ClientsWithoutUsableAddress, Is.EqualTo(2));
        Assert.That(result.Candidates, Is.Empty);
        Assert.That(result.NearThresholdCandidates, Is.Empty);
    }

    [Test]
    public async Task CityNormalization_TrimAndCaseInsensitive_MatchesCustomerGroupingPlannerSemantics()
    {
        SetClients(ClientWithCity(" Bern "), ClientWithCity("bern"), ClientWithCity("BERN"));

        var result = await _handler.Handle(Query(), CancellationToken.None);

        Assert.That(result.Candidates, Has.Count.EqualTo(1));
        Assert.That(result.Candidates[0].ClientCount, Is.EqualTo(3));
    }

    [Test]
    public async Task PreferredAddress_PicksEmployeeType_OverOtherTypes()
    {
        var client = new Client
        {
            Id = Guid.NewGuid(),
            FirstName = "Test",
            Name = "Client",
            Type = EntityTypeEnum.Employee,
            Addresses = new List<Address>
            {
                new() { City = "Zurich", Type = AddressTypeEnum.InvoicingAddress, ValidFrom = new DateTime(2026, 1, 1) },
                new() { City = "Winterthur", Type = AddressTypeEnum.Employee, ValidFrom = new DateTime(2026, 1, 1) }
            }
        };
        SetClients(client, ClientWithCity("Winterthur"), ClientWithCity("Winterthur"));

        var result = await _handler.Handle(Query(), CancellationToken.None);

        Assert.That(result.Candidates, Has.Count.EqualTo(1));
        Assert.That(result.Candidates[0].City, Is.EqualTo("Winterthur"));
        Assert.That(result.Candidates[0].ClientCount, Is.EqualTo(3));
    }

    [Test]
    public async Task DeletedGroup_DoesNotSuppressCandidate()
    {
        _groupRepository.List().Returns(new List<Group> { new() { Name = "Bern", IsDeleted = true } });
        SetClients(ClientWithCity("Bern"), ClientWithCity("Bern"), ClientWithCity("Bern"));

        var result = await _handler.Handle(Query(), CancellationToken.None);

        Assert.That(result.Candidates, Has.Count.EqualTo(1));
        Assert.That(result.Candidates[0].City, Is.EqualTo("Bern"));
    }
}
