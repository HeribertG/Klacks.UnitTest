// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for CustomerGroupingPlanner: assigns a customer to the nearest geocoded group and retires
/// the coarser ancestor (canton) it replaces, leaves non-location memberships (qualification groups)
/// untouched, reports customers without coordinates as unassigned, and is a no-op for customers already
/// sitting in their nearest group.
/// </summary>

using Klacks.Api.Application.Services.Grouping;

namespace Klacks.UnitTest.Application.Services.Grouping;

[TestFixture]
public class CustomerGroupingPlannerTests
{
    private static readonly Guid CantonZh = Guid.NewGuid();
    private static readonly Guid CityZurich = Guid.NewGuid();
    private static readonly Guid CityWinterthur = Guid.NewGuid();
    private static readonly Guid QualificationGroup = Guid.NewGuid();

    private IClientRepository _clientRepository = null!;
    private IGroupRepository _groupRepository = null!;
    private CustomerGroupingPlanner _planner = null!;

    [SetUp]
    public void Setup()
    {
        _clientRepository = Substitute.For<IClientRepository>();
        _groupRepository = Substitute.For<IGroupRepository>();
        _planner = new CustomerGroupingPlanner(_clientRepository, _groupRepository);

        _groupRepository.List().Returns(new List<Group>
        {
            new() { Id = CantonZh, Name = "ZH", Root = CantonZh, Lft = 1, Rgt = 6 },
            new() { Id = CityZurich, Name = "Zürich", Root = CantonZh, Lft = 2, Rgt = 3, Latitude = 47.3769, Longitude = 8.5417 },
            new() { Id = CityWinterthur, Name = "Winterthur", Root = CantonZh, Lft = 4, Rgt = 5, Latitude = 47.5000, Longitude = 8.7241 },
            new() { Id = QualificationGroup, Name = "Pflege Level 3", Root = QualificationGroup, Lft = 1, Rgt = 2 }
        });
    }

    [Test]
    public async Task BuildProposal_AssignsCustomerToNearestCity_AndRetiresCanton()
    {
        var customer = Customer("Anna", "Meier", 47.38, 8.54, new[] { CantonZh });
        SetCustomers(customer);

        var proposal = await _planner.BuildProposalAsync();

        proposal.AnchorGroupCount.ShouldBe(2);
        var assignment = proposal.Assignments.ShouldHaveSingleItem();
        assignment.TargetGroupId.ShouldBe(CityZurich);
        assignment.RetireGroupIds.ShouldContain(CantonZh);
    }

    [Test]
    public async Task BuildProposal_KeepsQualificationMembership()
    {
        var customer = Customer("Bea", "Huber", 47.50, 8.72, new[] { CantonZh, QualificationGroup });
        SetCustomers(customer);

        var proposal = await _planner.BuildProposalAsync();

        var assignment = proposal.Assignments.ShouldHaveSingleItem();
        assignment.TargetGroupId.ShouldBe(CityWinterthur);
        assignment.RetireGroupIds.ShouldContain(CantonZh);
        assignment.RetireGroupIds.ShouldNotContain(QualificationGroup);
    }

    [Test]
    public async Task BuildProposal_CustomerWithoutCoordinates_IsUnassigned()
    {
        var customer = Customer("Cara", "Frei", null, null, new[] { CantonZh });
        SetCustomers(customer);

        var proposal = await _planner.BuildProposalAsync();

        proposal.Assignments.ShouldBeEmpty();
        proposal.Unassigned.ShouldHaveSingleItem().Reason.ShouldBe("no geocoded address");
    }

    [Test]
    public async Task BuildProposal_CustomerAlreadyInNearestCity_IsNoOp()
    {
        var customer = Customer("Dora", "Lang", 47.38, 8.54, new[] { CityZurich });
        SetCustomers(customer);

        var proposal = await _planner.BuildProposalAsync();

        proposal.Assignments.ShouldBeEmpty();
        proposal.Unassigned.ShouldBeEmpty();
    }

    [Test]
    public async Task BuildProposal_NearestCityInOtherCanton_RetiresOldCantonToo()
    {
        var cantonBs = Guid.NewGuid();
        var cityBasel = Guid.NewGuid();
        var cantonBl = Guid.NewGuid();
        var cityReinach = Guid.NewGuid();
        _groupRepository.List().Returns(new List<Group>
        {
            new() { Id = cantonBs, Name = "BS", Root = cantonBs, Lft = 1, Rgt = 4 },
            new() { Id = cityBasel, Name = "Basel", Root = cantonBs, Lft = 2, Rgt = 3, Latitude = 47.56, Longitude = 7.59 },
            new() { Id = cantonBl, Name = "BL", Root = cantonBl, Lft = 1, Rgt = 4 },
            new() { Id = cityReinach, Name = "Reinach BL", Root = cantonBl, Lft = 2, Rgt = 3, Latitude = 47.49, Longitude = 7.59 }
        });
        var customer = Customer("Edi", "Roth", 47.49, 7.59, new[] { cantonBs });
        SetCustomers(customer);

        var proposal = await _planner.BuildProposalAsync();

        var assignment = proposal.Assignments.ShouldHaveSingleItem();
        assignment.TargetGroupId.ShouldBe(cityReinach);
        assignment.RetireGroupIds.ShouldContain(cantonBs);
    }

    [Test]
    public async Task BuildProposal_CantonWithoutOwnGeocodedCity_IsStillRetired()
    {
        var region = Guid.NewGuid();
        var cantonWithCity = Guid.NewGuid();
        var city = Guid.NewGuid();
        var cantonWithoutCity = Guid.NewGuid();
        _groupRepository.List().Returns(new List<Group>
        {
            new() { Id = region, Name = "Region", Root = region, Lft = 1, Rgt = 8 },
            new() { Id = cantonWithCity, Name = "A", Root = region, Lft = 2, Rgt = 5 },
            new() { Id = city, Name = "CityA", Root = region, Lft = 3, Rgt = 4, Latitude = 47.0, Longitude = 8.0 },
            new() { Id = cantonWithoutCity, Name = "B", Root = region, Lft = 6, Rgt = 7 }
        });
        var customer = Customer("Fritz", "Keller", 47.0, 8.0, new[] { cantonWithoutCity });
        SetCustomers(customer);

        var proposal = await _planner.BuildProposalAsync();

        var assignment = proposal.Assignments.ShouldHaveSingleItem();
        assignment.TargetGroupId.ShouldBe(city);
        assignment.RetireGroupIds.ShouldContain(cantonWithoutCity);
    }

    private void SetCustomers(params Client[] customers)
    {
        _clientRepository
            .GetByTypeWithAddressesAndGroupItemsAsync(EntityTypeEnum.Customer, Arg.Any<CancellationToken>())
            .Returns(customers.ToList());
    }

    private static Client Customer(string firstName, string lastName, double? lat, double? lon, IEnumerable<Guid> groupIds)
    {
        var clientId = Guid.NewGuid();
        var addresses = new List<Address>();
        if (lat.HasValue && lon.HasValue)
        {
            addresses.Add(new Address { ClientId = clientId, Latitude = lat, Longitude = lon });
        }

        return new Client
        {
            Id = clientId,
            FirstName = firstName,
            Name = lastName,
            Type = EntityTypeEnum.Customer,
            Addresses = addresses,
            GroupItems = groupIds.Select(g => new GroupItem { Id = Guid.NewGuid(), ClientId = clientId, GroupId = g }).ToList()
        };
    }
}
