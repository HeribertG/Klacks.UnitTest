// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for PartitionClientsByAddressCommandHandler: a preview (Apply=false) never touches a
/// repository or the unit of work, an apply creates the missing groups top-down and the memberships in
/// one commit, an already-existing group (by name under the right parent) is reused instead of
/// duplicated, a client that already holds the target membership is left untouched (the shape a second
/// apply run takes), and a verification mismatch rolls the whole write back by throwing.
/// </summary>

using Klacks.Api.Application.Commands.Groups;
using Klacks.Api.Application.DTOs.Groups;
using Klacks.Api.Application.Handlers.Groups;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Domain.Common;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Interfaces.Geo;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Settings;

namespace Klacks.UnitTest.Handlers.Groups;

[TestFixture]
public class PartitionClientsByAddressCommandHandlerTests
{
    private static readonly DateTime CompanyToday = new(2099, 1, 15, 0, 0, 0, DateTimeKind.Utc);

    private IClientRepository _clientRepository = null!;
    private IGroupRepository _groupRepository = null!;
    private IGroupItemRepository _groupItemRepository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private ICompanyClock _companyClock = null!;
    private ICountryRegionProvider _regionProvider = null!;
    private ICountryResolver _countryResolver = null!;
    private IStateRepository _stateRepository = null!;
    private ISettingsReader _settingsReader = null!;
    private PartitionClientsByAddressCommandHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        _clientRepository = Substitute.For<IClientRepository>();
        _groupRepository = Substitute.For<IGroupRepository>();
        _groupItemRepository = Substitute.For<IGroupItemRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _companyClock = Substitute.For<ICompanyClock>();
        _companyClock.GetTodayAsync(Arg.Any<CancellationToken>()).Returns(CompanyToday);

        _regionProvider = Substitute.For<ICountryRegionProvider>();
        _regionProvider.GetRegionByStateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["BE"] = "Deutschschweiz Mitte", ["ZH"] = "Deutschschweiz Zürich" });
        _countryResolver = Substitute.For<ICountryResolver>();
        _countryResolver.GetDefaultAsync(Arg.Any<CancellationToken>())
            .Returns(new Countries { Id = Guid.NewGuid(), Abbreviation = "CH" });
        _stateRepository = Substitute.For<IStateRepository>();
        _stateRepository.List().Returns(new List<State>
        {
            new() { Id = Guid.NewGuid(), Abbreviation = "BE", CountryPrefix = "CH", Name = StateName("Bern") }
        });
        _settingsReader = Substitute.For<ISettingsReader>();
        _settingsReader.GetSetting(SettingKeys.DefaultLanguage).Returns(new Settings { Value = "fr" });

        _handler = new PartitionClientsByAddressCommandHandler(
            _clientRepository, _groupRepository, _groupItemRepository, _unitOfWork, _companyClock,
            _regionProvider, _countryResolver, _stateRepository, _settingsReader);

        _groupRepository.List().Returns(new List<Group>());
        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<PartitionApplyOutcome>>>())
            .Returns(ci => ci.Arg<Func<Task<PartitionApplyOutcome>>>()());
        _groupItemRepository.GetByClientAndGroup(Arg.Any<Guid>(), Arg.Any<Guid>()).Returns((GroupItem?)null);
        _groupItemRepository.CountExistingByIds(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<IReadOnlyCollection<Guid>>().Count);
    }

    private static MultiLanguage StateName(string german)
    {
        var name = new MultiLanguage();
        name.SetValue("de", german);
        return name;
    }

    private static PartitionClientsByAddressCommand Command(
        bool apply,
        GroupPartitionLevelEnum level = GroupPartitionLevelEnum.State,
        bool includeAlreadyGrouped = false,
        DateTime? validFrom = null,
        IReadOnlyList<EntityTypeEnum>? entityTypes = null,
        int clusterSharePercent = 10) =>
        new(level, entityTypes ?? [EntityTypeEnum.Employee], RootGroupId: null, RootGroupName: null,
            includeAlreadyGrouped, validFrom, apply, "tester", clusterSharePercent);

    private static Client Bern()
    {
        var clientId = Guid.NewGuid();
        return new Client
        {
            Id = clientId,
            FirstName = "Anna",
            Name = "Meier",
            Type = EntityTypeEnum.Employee,
            Addresses = new List<Address>
            {
                new() { ClientId = clientId, Type = AddressTypeEnum.Employee, State = "BE", City = "Bern", Country = "CH", ValidFrom = CompanyToday }
            }
        };
    }

    [Test]
    public async Task Preview_DoesNotPersistAnything()
    {
        _clientRepository.GetByTypeWithAddressesAndGroupItemsAsync(EntityTypeEnum.Employee, Arg.Any<CancellationToken>())
            .Returns(new List<Client> { Bern() });

        var result = await _handler.Handle(Command(apply: false), CancellationToken.None);

        result.Applied.ShouldBeFalse();
        result.AssignedCount.ShouldBe(0);
        result.Groups.Count.ShouldBe(2);
        await _groupRepository.DidNotReceive().Add(Arg.Any<Group>());
        await _groupItemRepository.DidNotReceive().Add(Arg.Any<GroupItem>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Apply_CreatesMissingGroupsTopDown_AndMemberships_CommitsOnce()
    {
        _clientRepository.GetByTypeWithAddressesAndGroupItemsAsync(EntityTypeEnum.Employee, Arg.Any<CancellationToken>())
            .Returns(new List<Client> { Bern() });

        var result = await _handler.Handle(Command(apply: true), CancellationToken.None);

        result.Applied.ShouldBeTrue();
        result.AssignedCount.ShouldBe(1);
        result.VerifiedCount.ShouldBe(1);
        result.Groups.ShouldAllBe(g => g.GroupId != null);
        result.Groups.ShouldAllBe(g => !g.Existed);
        await _groupRepository.Received(2).Add(Arg.Any<Group>());
        await _groupItemRepository.Received(1).Add(Arg.Any<GroupItem>());
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task Apply_ReusesExistingGroup_ByNameUnderTheRightParent_InsteadOfCreatingIt()
    {
        var regionId = Guid.NewGuid();
        var beId = Guid.NewGuid();
        _groupRepository.List().Returns(new List<Group>
        {
            new() { Id = regionId, Name = "Deutschschweiz Mitte", Parent = null },
            new() { Id = beId, Name = "BE", Parent = regionId }
        });
        _clientRepository.GetByTypeWithAddressesAndGroupItemsAsync(EntityTypeEnum.Employee, Arg.Any<CancellationToken>())
            .Returns(new List<Client> { Bern() });

        var result = await _handler.Handle(Command(apply: true), CancellationToken.None);

        await _groupRepository.DidNotReceive().Add(Arg.Any<Group>());
        await _groupItemRepository.Received(1).Add(Arg.Is<GroupItem>(gi => gi.GroupId == beId));
        result.Groups.Single(g => g.Name == "BE").GroupId.ShouldBe(beId);
        result.Groups.Single(g => g.Name == "BE").Existed.ShouldBeTrue();
    }

    [Test]
    public async Task Apply_ClientAlreadyMemberOfTargetGroup_IsANoOp_LikeASecondRun()
    {
        var regionId = Guid.NewGuid();
        var beId = Guid.NewGuid();
        var client = Bern();
        _groupRepository.List().Returns(new List<Group>
        {
            new() { Id = regionId, Name = "Deutschschweiz Mitte", Parent = null },
            new() { Id = beId, Name = "BE", Parent = regionId }
        });
        _clientRepository.GetByTypeWithAddressesAndGroupItemsAsync(EntityTypeEnum.Employee, Arg.Any<CancellationToken>())
            .Returns(new List<Client> { client });
        _groupItemRepository.GetByClientAndGroup(client.Id, beId)
            .Returns(new GroupItem { Id = Guid.NewGuid(), ClientId = client.Id, GroupId = beId });

        var result = await _handler.Handle(Command(apply: true), CancellationToken.None);

        result.AssignedCount.ShouldBe(0);
        result.AlreadyMemberCount.ShouldBe(1);
        result.VerifiedCount.ShouldBe(0);
        await _groupItemRepository.DidNotReceive().Add(Arg.Any<GroupItem>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public void Apply_RollsBackByThrowing_WhenVerificationCountDoesNotMatch()
    {
        _clientRepository.GetByTypeWithAddressesAndGroupItemsAsync(EntityTypeEnum.Employee, Arg.Any<CancellationToken>())
            .Returns(new List<Client> { Bern() });
        _groupItemRepository.CountExistingByIds(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(0);

        Assert.ThrowsAsync<SkillVerificationException>(
            () => _handler.Handle(Command(apply: true), CancellationToken.None));
    }

    [Test]
    public async Task Apply_StampsTheRequestedValidFrom_OnNewGroupsAndMemberships()
    {
        var validFrom = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        _clientRepository.GetByTypeWithAddressesAndGroupItemsAsync(EntityTypeEnum.Employee, Arg.Any<CancellationToken>())
            .Returns(new List<Client> { Bern() });

        await _handler.Handle(Command(apply: true, validFrom: validFrom), CancellationToken.None);

        await _groupRepository.Received().Add(Arg.Is<Group>(g => g.ValidFrom == validFrom));
        await _groupItemRepository.Received(1).Add(Arg.Is<GroupItem>(gi => gi.ValidFrom == validFrom));
    }

    [Test]
    public async Task Apply_DefaultsToCompanyToday_WhenNoValidFromGiven()
    {
        _clientRepository.GetByTypeWithAddressesAndGroupItemsAsync(EntityTypeEnum.Employee, Arg.Any<CancellationToken>())
            .Returns(new List<Client> { Bern() });

        await _handler.Handle(Command(apply: true), CancellationToken.None);

        await _groupItemRepository.Received(1).Add(Arg.Is<GroupItem>(gi => gi.ValidFrom == CompanyToday));
    }

    [Test]
    public async Task Handle_AllThreeTypes_LoadsEachTypeOnceAndReportsAll()
    {
        var employee = Bern();
        var customer = Bern();
        customer.Type = EntityTypeEnum.Customer;
        _clientRepository.GetByTypeWithAddressesAndGroupItemsAsync(EntityTypeEnum.Employee, Arg.Any<CancellationToken>()).Returns([employee]);
        _clientRepository.GetByTypeWithAddressesAndGroupItemsAsync(EntityTypeEnum.ExternEmp, Arg.Any<CancellationToken>()).Returns([]);
        _clientRepository.GetByTypeWithAddressesAndGroupItemsAsync(EntityTypeEnum.Customer, Arg.Any<CancellationToken>()).Returns([customer]);

        var result = await _handler.Handle(
            Command(apply: false, entityTypes: [EntityTypeEnum.Employee, EntityTypeEnum.ExternEmp, EntityTypeEnum.Customer]),
            CancellationToken.None);

        result.TotalClients.ShouldBe(2);
        result.EntityType.ShouldBe("All");
        await _clientRepository.Received(1).GetByTypeWithAddressesAndGroupItemsAsync(EntityTypeEnum.Customer, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_Preview_UsesRegionMapOfTheAddressCountry_AndStateNameAsDescription()
    {
        _clientRepository.GetByTypeWithAddressesAndGroupItemsAsync(EntityTypeEnum.Employee, Arg.Any<CancellationToken>()).Returns([Bern()]);

        var result = await _handler.Handle(Command(apply: false), CancellationToken.None);

        result.Groups.ShouldContain(g => g.Name == "Deutschschweiz Mitte");
        result.Groups.Single(g => g.Name == "BE").ParentName.ShouldBe("Deutschschweiz Mitte");
        await _regionProvider.Received(1).GetRegionByStateAsync("CH", Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_AddressWithoutCountry_AsksRegionProviderForCompanyDefaultCountry()
    {
        var bern = Bern();
        bern.Addresses.First().Country = string.Empty;
        _clientRepository.GetByTypeWithAddressesAndGroupItemsAsync(EntityTypeEnum.Employee, Arg.Any<CancellationToken>()).Returns([bern]);

        await _handler.Handle(Command(apply: false), CancellationToken.None);

        await _regionProvider.Received(1).GetRegionByStateAsync("CH", Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_ApplyClusterLevel_WritesCentroidAndMarksGeocodingAttempted_OnClusterGroupsOnly()
    {
        var clients = Enumerable.Range(0, 3).Select(_ => Bern()).ToList();
        foreach (var client in clients)
        {
            client.Addresses.First().Latitude = 46.95;
            client.Addresses.First().Longitude = 7.45;
        }

        _clientRepository.GetByTypeWithAddressesAndGroupItemsAsync(EntityTypeEnum.Employee, Arg.Any<CancellationToken>()).Returns(clients);
        var added = new List<Group>();
        await _groupRepository.Add(Arg.Do<Group>(added.Add));

        var result = await _handler.Handle(Command(apply: true, level: GroupPartitionLevelEnum.Cluster), CancellationToken.None);

        result.Applied.ShouldBeTrue();
        var cluster = added.Single(g => g.Name == "Bern");
        cluster.Latitude!.Value.ShouldBe(46.95, 0.0001);
        cluster.Longitude!.Value.ShouldBe(7.45, 0.0001);
        cluster.GeocodingAttempted.ShouldBeTrue();
        var state = added.Single(g => g.Name == "BE");
        state.Latitude.ShouldBeNull();
        state.GeocodingAttempted.ShouldBeFalse();
        state.Description.ShouldBe("Bern");
        cluster.Parent.ShouldBe(state.Id);
    }

    [Test]
    public async Task Handle_Preview_NeverCallsRegionProviderForClientsWithoutAddress()
    {
        var noAddress = Bern();
        noAddress.Addresses.Clear();
        _clientRepository.GetByTypeWithAddressesAndGroupItemsAsync(EntityTypeEnum.Employee, Arg.Any<CancellationToken>()).Returns([noAddress]);

        var result = await _handler.Handle(Command(apply: false), CancellationToken.None);

        result.UnassignableCount.ShouldBe(1);
        await _regionProvider.DidNotReceive().GetRegionByStateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_Preview_IgnoresStatesOfCountriesNotInPlay()
    {
        _stateRepository.List().Returns(new List<State>
        {
            new() { Id = Guid.NewGuid(), Abbreviation = "BE", CountryPrefix = "CH", Name = StateName("Bern") },
            new() { Id = Guid.NewGuid(), Abbreviation = "BY", CountryPrefix = "DE", Name = StateName("Bayern") }
        });
        _clientRepository.GetByTypeWithAddressesAndGroupItemsAsync(EntityTypeEnum.Employee, Arg.Any<CancellationToken>()).Returns([Bern()]);
        var added = new List<Group>();
        await _groupRepository.Add(Arg.Do<Group>(added.Add));

        var result = await _handler.Handle(Command(apply: true), CancellationToken.None);

        result.Applied.ShouldBeTrue();
        added.Single(g => g.Name == "BE").Description.ShouldBe("Bern");
        added.ShouldNotContain(g => g.Description == "Bayern");
    }

    [Test]
    public async Task Handle_StateDescription_PrefersCompanyDefaultLanguage()
    {
        var stateName = new MultiLanguage();
        stateName.SetValue("de", "Bern");
        stateName.SetValue("fr", "Berne");
        _stateRepository.List().Returns(new List<State>
        {
            new() { Id = Guid.NewGuid(), Abbreviation = "BE", CountryPrefix = "CH", Name = stateName }
        });
        _clientRepository.GetByTypeWithAddressesAndGroupItemsAsync(EntityTypeEnum.Employee, Arg.Any<CancellationToken>()).Returns([Bern()]);
        var added = new List<Group>();
        await _groupRepository.Add(Arg.Do<Group>(added.Add));
        _settingsReader.GetSetting(SettingKeys.DefaultLanguage).Returns(new Settings { Value = "fr" });

        await _handler.Handle(Command(apply: true), CancellationToken.None);

        added.Single(g => g.Name == "BE").Description.ShouldBe("Berne");
    }

    [Test]
    public async Task Handle_StateDescription_FallsBackToCoreLanguages_WhenDefaultLanguageIsBlank()
    {
        var stateName = new MultiLanguage();
        stateName.SetValue("de", "Bern");
        stateName.SetValue("fr", "Berne");
        _stateRepository.List().Returns(new List<State>
        {
            new() { Id = Guid.NewGuid(), Abbreviation = "BE", CountryPrefix = "CH", Name = stateName }
        });
        _clientRepository.GetByTypeWithAddressesAndGroupItemsAsync(EntityTypeEnum.Employee, Arg.Any<CancellationToken>()).Returns([Bern()]);
        var added = new List<Group>();
        await _groupRepository.Add(Arg.Do<Group>(added.Add));
        _settingsReader.GetSetting(SettingKeys.DefaultLanguage).Returns((Settings?)null);

        await _handler.Handle(Command(apply: true), CancellationToken.None);

        added.Single(g => g.Name == "BE").Description.ShouldBe("Bern");
    }
}
