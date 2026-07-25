// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for CustomerGroupingPlanner: an exact city-name match takes precedence over the geographic
/// nearest-anchor path (and works when no group carries coordinates at all), duplicate group names are
/// never matched, the main address wins over workplace/invoicing addresses while those still serve as a
/// fallback, the coarser ancestor (canton) it replaces is retired, non-location memberships (qualification
/// groups) stay untouched, clients that cannot be placed get a distinguishable reason, and a client already
/// sitting in its target group is a no-op. Scenario memberships (AnalyseToken set) are invisible to the
/// planner: they are never retired and never make a client count as already placed, and the retired
/// memberships are reported with their group names so a preview can show what would end.
/// </summary>

using Klacks.Api.Application.Services.Grouping;

namespace Klacks.UnitTest.Application.Services.Grouping;

[TestFixture]
public class CustomerGroupingPlannerTests
{
    private const string ReasonNoUsableAddress = "no address with a city or coordinates";
    private const string ReasonNoAnchors = "no group carries coordinates and no group name matches an address city";
    private const string ReasonNoGeoAnchors = "address city matches no group name and no group carries coordinates";
    private const string ReasonNoMatch = "address city matches no group name and the address has no coordinates";

    private const string MatchCityNameMainAddress = "city name (main address)";
    private const string MatchCityNameWorkplaceAddress = "city name (workplace address)";
    private const string MatchCityNameInvoicingAddress = "city name (invoicing address)";
    private const string MatchCoordinatesMainAddress = "nearest coordinates (main address)";

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
        assignment.MatchReason.ShouldBe(MatchCoordinatesMainAddress);
        assignment.DistanceKm.ShouldNotBeNull();
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
    public async Task BuildProposal_ScenarioMembership_IsNotRetired()
    {
        var customer = Customer("Tom", "Widmer", 47.38, 8.54, new[] { CantonZh });
        AddScenarioMembership(customer, CityWinterthur);
        SetCustomers(customer);

        var proposal = await _planner.BuildProposalAsync();

        var assignment = proposal.Assignments.ShouldHaveSingleItem();
        assignment.TargetGroupId.ShouldBe(CityZurich);
        assignment.RetireGroupIds.ShouldBe(new[] { CantonZh });
        assignment.RetireGroupIds.ShouldNotContain(CityWinterthur);
        assignment.CurrentGroupNames.ShouldBe(new[] { "ZH" });
    }

    [Test]
    public async Task BuildProposal_OnlyScenarioMembershipInTargetGroup_StillPlansTheRealMove()
    {
        var customer = Customer("Uwe", "Ammann", 47.38, 8.54, Array.Empty<Guid>());
        AddScenarioMembership(customer, CityZurich);
        SetCustomers(customer);

        var proposal = await _planner.BuildProposalAsync();

        var assignment = proposal.Assignments.ShouldHaveSingleItem();
        assignment.TargetGroupId.ShouldBe(CityZurich);
        assignment.CurrentGroupNames.ShouldBeEmpty();
    }

    [Test]
    public async Task BuildProposal_RetireGroupNames_NameTheRetiredMemberships()
    {
        var customer = Customer("Vera", "Sigg", 47.38, 8.54, new[] { CantonZh, QualificationGroup });
        SetCustomers(customer);

        var proposal = await _planner.BuildProposalAsync();

        var assignment = proposal.Assignments.ShouldHaveSingleItem();
        assignment.RetireGroupNames.ShouldBe(new[] { "ZH" });
        assignment.RetireGroupNames.Count.ShouldBe(assignment.RetireGroupIds.Count);
    }

    [Test]
    public async Task BuildProposal_ClientWithoutAnyAddress_IsUnassignedWithNoAddressReason()
    {
        var customer = Customer("Cara", "Frei", null, null, new[] { CantonZh });
        SetCustomers(customer);

        var proposal = await _planner.BuildProposalAsync();

        proposal.Assignments.ShouldBeEmpty();
        var unassigned = proposal.Unassigned.ShouldHaveSingleItem();
        unassigned.ClientName.ShouldBe("Cara Frei");
        unassigned.Reason.ShouldBe(ReasonNoUsableAddress);
    }

    [Test]
    public async Task BuildProposal_CityMatchesNoGroupAndNoCoordinates_IsUnassignedWithNoMatchReason()
    {
        var customer = CustomerWithAddresses("Gina", "Vogel", new[] { CantonZh }, AddressWith("Genf"));
        SetCustomers(customer);

        var proposal = await _planner.BuildProposalAsync();

        proposal.Assignments.ShouldBeEmpty();
        proposal.Unassigned.ShouldHaveSingleItem().Reason.ShouldBe(ReasonNoMatch);
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

    [Test]
    public async Task BuildProposal_CityNameMatch_WinsOverNearerGeocodedGroup()
    {
        var canton = Guid.NewGuid();
        var cityZurich = Guid.NewGuid();
        var cityWinterthur = Guid.NewGuid();
        _groupRepository.List().Returns(new List<Group>
        {
            new() { Id = canton, Name = "ZH", Root = canton, Lft = 1, Rgt = 6 },
            new() { Id = cityZurich, Name = "Zürich", Root = canton, Lft = 2, Rgt = 3 },
            new() { Id = cityWinterthur, Name = "Winterthur", Root = canton, Lft = 4, Rgt = 5, Latitude = 47.5000, Longitude = 8.7241 }
        });
        var customer = CustomerWithAddresses(
            "Hans", "Berg", new[] { canton }, AddressWith("Zürich", 47.5000, 8.7241));
        SetCustomers(customer);

        var proposal = await _planner.BuildProposalAsync();

        proposal.AnchorGroupCount.ShouldBe(2);
        var assignment = proposal.Assignments.ShouldHaveSingleItem();
        assignment.TargetGroupId.ShouldBe(cityZurich);
        assignment.DistanceKm.ShouldBeNull();
        assignment.MatchReason.ShouldBe(MatchCityNameMainAddress);
        assignment.RetireGroupIds.ShouldContain(canton);
    }

    [Test]
    public async Task BuildProposal_NoGroupHasCoordinates_StillAssignsByCityName_AndRetiresCanton()
    {
        var canton = Guid.NewGuid();
        var cityZurich = Guid.NewGuid();
        _groupRepository.List().Returns(new List<Group>
        {
            new() { Id = canton, Name = "ZH", Root = canton, Lft = 1, Rgt = 4 },
            new() { Id = cityZurich, Name = " zürich ", Root = canton, Lft = 2, Rgt = 3 }
        });
        var customer = CustomerWithAddresses("Ida", "Stark", new[] { canton }, AddressWith("Zürich"));
        SetCustomers(customer);

        var proposal = await _planner.BuildProposalAsync();

        proposal.AnchorGroupCount.ShouldBe(1);
        var assignment = proposal.Assignments.ShouldHaveSingleItem();
        assignment.TargetGroupId.ShouldBe(cityZurich);
        assignment.DistanceKm.ShouldBeNull();
        assignment.RetireGroupIds.ShouldContain(canton);
    }

    [Test]
    public async Task BuildProposal_DuplicateGroupName_IsNotMatched_AndReportsNoAnchors()
    {
        var firstZurich = Guid.NewGuid();
        var secondZurich = Guid.NewGuid();
        _groupRepository.List().Returns(new List<Group>
        {
            new() { Id = firstZurich, Name = "Zürich", Root = firstZurich, Lft = 1, Rgt = 2 },
            new() { Id = secondZurich, Name = "Zürich", Root = secondZurich, Lft = 1, Rgt = 2 }
        });
        var customer = CustomerWithAddresses("Jan", "Weber", Array.Empty<Guid>(), AddressWith("Zürich"));
        SetCustomers(customer);

        var proposal = await _planner.BuildProposalAsync();

        proposal.AnchorGroupCount.ShouldBe(0);
        proposal.Assignments.ShouldBeEmpty();
        proposal.Unassigned.ShouldHaveSingleItem().Reason.ShouldBe(ReasonNoAnchors);
    }

    [Test]
    public async Task BuildProposal_HasCoordinatesButNoGroupIsGeocoded_ReportsNoGeoAnchorsReason()
    {
        var cityBern = Guid.NewGuid();
        _groupRepository.List().Returns(new List<Group>
        {
            new() { Id = cityBern, Name = "Bern", Root = cityBern, Lft = 1, Rgt = 2 }
        });
        var matched = CustomerWithAddresses("Rita", "Good", Array.Empty<Guid>(), AddressWith("Bern"));
        var stranded = CustomerWithAddresses(
            "Sven", "Bach", Array.Empty<Guid>(), AddressWith("Genf", 46.2044, 6.1432));
        SetCustomers(matched, stranded);

        var proposal = await _planner.BuildProposalAsync();

        proposal.AnchorGroupCount.ShouldBe(1);
        proposal.Assignments.ShouldHaveSingleItem().TargetGroupId.ShouldBe(cityBern);
        proposal.Unassigned.ShouldHaveSingleItem().Reason.ShouldBe(ReasonNoGeoAnchors);
    }

    [Test]
    public async Task BuildProposal_DuplicateGroupName_FallsBackToNearestGeocodedGroup()
    {
        var firstZurich = Guid.NewGuid();
        var secondZurich = Guid.NewGuid();
        var cityBern = Guid.NewGuid();
        _groupRepository.List().Returns(new List<Group>
        {
            new() { Id = firstZurich, Name = "Zürich", Root = firstZurich, Lft = 1, Rgt = 2 },
            new() { Id = secondZurich, Name = "Zürich", Root = secondZurich, Lft = 1, Rgt = 2 },
            new() { Id = cityBern, Name = "Bern", Root = cityBern, Lft = 1, Rgt = 2, Latitude = 46.9480, Longitude = 7.4474 }
        });
        var customer = CustomerWithAddresses(
            "Kim", "Frei", Array.Empty<Guid>(), AddressWith("Zürich", 46.9480, 7.4474));
        SetCustomers(customer);

        var proposal = await _planner.BuildProposalAsync();

        var assignment = proposal.Assignments.ShouldHaveSingleItem();
        assignment.TargetGroupId.ShouldBe(cityBern);
        assignment.MatchReason.ShouldBe(MatchCoordinatesMainAddress);
        assignment.DistanceKm.ShouldNotBeNull();
    }

    [Test]
    public async Task BuildProposal_OnlyWorkplaceAddress_IsMatchedByCityName()
    {
        var customer = CustomerWithAddresses(
            "Lea", "Suter",
            new[] { CantonZh },
            AddressWith("Winterthur", type: AddressTypeEnum.Workplace));
        SetCustomers(customer);

        var proposal = await _planner.BuildProposalAsync();

        var assignment = proposal.Assignments.ShouldHaveSingleItem();
        assignment.TargetGroupId.ShouldBe(CityWinterthur);
        assignment.MatchReason.ShouldBe(MatchCityNameWorkplaceAddress);
        assignment.RetireGroupIds.ShouldContain(CantonZh);
    }

    [Test]
    public async Task BuildProposal_OnlyInvoicingAddress_IsMatchedByCityName()
    {
        var customer = CustomerWithAddresses(
            "Mia", "Brun",
            new[] { CantonZh },
            AddressWith("Winterthur", type: AddressTypeEnum.InvoicingAddress));
        SetCustomers(customer);

        var proposal = await _planner.BuildProposalAsync();

        var assignment = proposal.Assignments.ShouldHaveSingleItem();
        assignment.TargetGroupId.ShouldBe(CityWinterthur);
        assignment.MatchReason.ShouldBe(MatchCityNameInvoicingAddress);
    }

    [Test]
    public async Task BuildProposal_MainAddress_WinsOverWorkplaceAddress()
    {
        var customer = CustomerWithAddresses(
            "Nora", "Kunz",
            new[] { CantonZh },
            AddressWith("Zürich", type: AddressTypeEnum.Workplace, validFrom: new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
            AddressWith("Winterthur", validFrom: new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        SetCustomers(customer);

        var proposal = await _planner.BuildProposalAsync();

        var assignment = proposal.Assignments.ShouldHaveSingleItem();
        assignment.TargetGroupId.ShouldBe(CityWinterthur);
        assignment.MatchReason.ShouldBe(MatchCityNameMainAddress);
    }

    [Test]
    public async Task BuildProposal_NewestMainAddress_Wins()
    {
        var customer = CustomerWithAddresses(
            "Olga", "Hess",
            new[] { CantonZh },
            AddressWith("Zürich", validFrom: new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
            AddressWith("Winterthur", validFrom: new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        SetCustomers(customer);

        var proposal = await _planner.BuildProposalAsync();

        proposal.Assignments.ShouldHaveSingleItem().TargetGroupId.ShouldBe(CityWinterthur);
    }

    [Test]
    public async Task BuildProposal_DeletedAddress_IsIgnored()
    {
        var deleted = AddressWith("Winterthur", validFrom: new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        deleted.IsDeleted = true;
        var active = AddressWith("Zürich", validFrom: new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var customer = CustomerWithAddresses("Pia", "Roth", new[] { CantonZh }, deleted, active);
        SetCustomers(customer);

        var proposal = await _planner.BuildProposalAsync();

        proposal.Assignments.ShouldHaveSingleItem().TargetGroupId.ShouldBe(CityZurich);
    }

    [Test]
    public async Task BuildProposal_ForEmployees_QueriesEmployeeType_AndAssignsToNearestCity()
    {
        var employee = ClientOfType(EntityTypeEnum.Employee, "Gina", "Vogel", 47.38, 8.54, new[] { CantonZh });
        _clientRepository
            .GetByTypeWithAddressesAndGroupItemsAsync(EntityTypeEnum.Employee, Arg.Any<CancellationToken>())
            .Returns(new List<Client> { employee });

        var proposal = await _planner.BuildProposalAsync(EntityTypeEnum.Employee);

        var assignment = proposal.Assignments.ShouldHaveSingleItem();
        assignment.TargetGroupId.ShouldBe(CityZurich);
        assignment.RetireGroupIds.ShouldContain(CantonZh);
    }

    [Test]
    public async Task BuildProposal_ForEmployees_DoesNotQueryCustomers()
    {
        var employee = ClientOfType(EntityTypeEnum.Employee, "Hans", "Berg", 47.38, 8.54, new[] { CantonZh });
        _clientRepository
            .GetByTypeWithAddressesAndGroupItemsAsync(EntityTypeEnum.Employee, Arg.Any<CancellationToken>())
            .Returns(new List<Client> { employee });

        await _planner.BuildProposalAsync(EntityTypeEnum.Employee);

        await _clientRepository.DidNotReceive()
            .GetByTypeWithAddressesAndGroupItemsAsync(EntityTypeEnum.Customer, Arg.Any<CancellationToken>());
        await _clientRepository.Received(1)
            .GetByTypeWithAddressesAndGroupItemsAsync(EntityTypeEnum.Employee, Arg.Any<CancellationToken>());
    }

    private static void AddScenarioMembership(Client client, Guid groupId)
    {
        client.GroupItems.Add(new GroupItem
        {
            Id = Guid.NewGuid(),
            ClientId = client.Id,
            GroupId = groupId,
            AnalyseToken = Guid.NewGuid()
        });
    }

    private void SetCustomers(params Client[] customers)
    {
        _clientRepository
            .GetByTypeWithAddressesAndGroupItemsAsync(EntityTypeEnum.Customer, Arg.Any<CancellationToken>())
            .Returns(customers.ToList());
    }

    private static Client Customer(string firstName, string lastName, double? lat, double? lon, IEnumerable<Guid> groupIds)
        => ClientOfType(EntityTypeEnum.Customer, firstName, lastName, lat, lon, groupIds);

    private static Client ClientOfType(
        EntityTypeEnum type, string firstName, string lastName, double? lat, double? lon, IEnumerable<Guid> groupIds)
    {
        var addresses = lat.HasValue && lon.HasValue
            ? new[] { AddressWith(string.Empty, lat, lon) }
            : Array.Empty<Address>();

        return BuildClient(type, firstName, lastName, groupIds, addresses);
    }

    private static Client CustomerWithAddresses(
        string firstName, string lastName, IEnumerable<Guid> groupIds, params Address[] addresses)
        => BuildClient(EntityTypeEnum.Customer, firstName, lastName, groupIds, addresses);

    private static Address AddressWith(
        string city,
        double? lat = null,
        double? lon = null,
        AddressTypeEnum type = AddressTypeEnum.Employee,
        DateTime? validFrom = null)
        => new()
        {
            City = city,
            Latitude = lat,
            Longitude = lon,
            Type = type,
            ValidFrom = validFrom
        };

    private static Client BuildClient(
        EntityTypeEnum type, string firstName, string lastName, IEnumerable<Guid> groupIds, Address[] addresses)
    {
        var clientId = Guid.NewGuid();
        foreach (var address in addresses)
        {
            address.ClientId = clientId;
        }

        return new Client
        {
            Id = clientId,
            FirstName = firstName,
            Name = lastName,
            Type = type,
            Addresses = addresses.ToList(),
            GroupItems = groupIds
                .Select(g => new GroupItem { Id = Guid.NewGuid(), ClientId = clientId, GroupId = g })
                .ToList()
        };
    }
}
