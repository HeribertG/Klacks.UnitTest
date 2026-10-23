// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Characterization tests for PartitionClientsByAddressCommandHandler.Handle: the exact restricted-caller message, the
/// write order inside the apply transaction (all missing groups top-down, then the membership lookups, then the new
/// memberships, one commit, then the verification read), the exact verification failure text and the pass-through of
/// the visibility advisory count, the level and the entity-type label.
/// </summary>

using Klacks.Api.Application.Commands.Groups;
using Klacks.Api.Application.DTOs.Groups;
using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Handlers.Groups;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Domain.Common;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Interfaces.Geo;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Geo;
using Klacks.Api.Domain.Models.Settings;
using Klacks.UnitTest.TestHelpers;
using Klacks.Api.Domain.Enums;

namespace Klacks.UnitTest.Handlers.Groups;

[TestFixture]
public class PartitionClientsByAddressCommandHandlerCharacterizationTests
{
    private static readonly DateTime CompanyToday = new(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc);

    private IClientRepository _clientRepository = null!;
    private IGroupRepository _groupRepository = null!;
    private IGroupItemRepository _groupItemRepository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private ICompanyClock _companyClock = null!;
    private ICountryRegionProvider _regionProvider = null!;
    private ICountryResolver _countryResolver = null!;
    private IStateRepository _stateRepository = null!;
    private ISettingsReader _settingsReader = null!;
    private IGroupVisibilityPreservationService _visibilityPreservation = null!;

    [SetUp]
    public void Setup()
    {
        _clientRepository = Substitute.For<IClientRepository>();
        _groupRepository = Substitute.For<IGroupRepository>();
        _groupItemRepository = Substitute.For<IGroupItemRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _companyClock = Substitute.For<ICompanyClock>();
        _companyClock.GetTodayAsync(Arg.Any<CancellationToken>()).Returns(CompanyToday);

        var regionNames = new MultiLanguage();
        regionNames.SetValue("de", "Deutschschweiz Mitte");
        IReadOnlyDictionary<string, CountryRegion> regionByState = new Dictionary<string, CountryRegion>(StringComparer.OrdinalIgnoreCase)
        {
            ["BE"] = new CountryRegion("mitte", regionNames)
        };
        _regionProvider = Substitute.For<ICountryRegionProvider>();
        _regionProvider.GetRegionByStateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(regionByState);
        _countryResolver = Substitute.For<ICountryResolver>();
        _countryResolver.GetDefaultAsync(Arg.Any<CancellationToken>())
            .Returns(new Countries { Id = Guid.NewGuid(), Abbreviation = "CH" });
        _stateRepository = Substitute.For<IStateRepository>();
        _stateRepository.List().Returns(new List<State>());
        _settingsReader = Substitute.For<ISettingsReader>();
        _settingsReader.GetSetting(SettingKeys.DefaultLanguage).Returns(new Settings { Value = "de" });
        _visibilityPreservation = Substitute.For<IGroupVisibilityPreservationService>();
        _visibilityPreservation.CountUsersKeepingFullVisibilityAsync(Arg.Any<CancellationToken>()).Returns(3);

        _groupRepository.List().Returns(new List<Group>());
        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<PartitionApplyOutcome>>>())
            .Returns(ci => ci.Arg<Func<Task<PartitionApplyOutcome>>>()());
        _groupItemRepository.GetByClientAndGroup(Arg.Any<Guid>(), Arg.Any<Guid>()).Returns((GroupItem?)null);
        _groupItemRepository.CountExistingByIds(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<IReadOnlyCollection<Guid>>().Count);
    }

    private PartitionClientsByAddressCommandHandler Handler(IGroupVisibilityGuard? guard = null) => new(
        _clientRepository, _groupRepository, _groupItemRepository, _unitOfWork,
        _regionProvider, _countryResolver, _stateRepository, _settingsReader, _visibilityPreservation,
        guard ?? TestGroupWriteVisibility.UnrestrictedGroups(), TestGroupMembershipStart.Planner(_companyClock));

    private static PartitionClientsByAddressCommand Command(bool apply) =>
        new(GroupPartitionLevelEnum.State, [EntityTypeEnum.Employee], RootGroupId: null, RootGroupName: null,
            false, null, apply, "tester", 10);

    private static Client Bern(string firstName)
    {
        var clientId = Guid.NewGuid();
        return new Client
        {
            Id = clientId,
            FirstName = firstName,
            Name = "Meier",
            Type = EntityTypeEnum.Employee,
            Addresses = new List<Address>
            {
                new() { ClientId = clientId, Type = AddressTypeEnum.Employee, State = "BE", City = "Bern", Country = "CH", ValidFrom = CompanyToday }
            }
        };
    }

    private void SetEmployees(params Client[] employees) =>
        _clientRepository.GetByTypeWithAddressesAndGroupItemsAsync(EntityTypeEnum.Employee, Arg.Any<CancellationToken>())
            .Returns(employees.ToList());

    [Test]
    public async Task RestrictedCaller_IsRefusedWithTheExactMessage()
    {
        var guard = Substitute.For<IGroupVisibilityGuard>();
        guard.IsUnrestrictedAsync(Arg.Any<CancellationToken>()).Returns(false);

        var ex = await Should.ThrowAsync<ForbiddenException>(
            () => Handler(guard).Handle(Command(apply: true), CancellationToken.None));

        ex.Message.ShouldBe("Only unrestricted users may partition clients into groups");
        await _groupRepository.DidNotReceive().List();
    }

    [Test]
    public async Task Preview_PassesTheAdvisoryCountLevelAndEntityTypeThrough()
    {
        SetEmployees(Bern("Anna"));

        var result = await Handler().Handle(Command(apply: false), CancellationToken.None);

        result.Applied.ShouldBeFalse();
        result.Level.ShouldBe("State");
        result.EntityType.ShouldBe("Employee");
        result.UsersKeepingFullVisibilityCount.ShouldBe(3);
        result.VerifiedCount.ShouldBe(0);
        result.AlreadyMemberCount.ShouldBe(0);
        result.Groups.ShouldAllBe(g => g.GroupId == null);
        await _unitOfWork.DidNotReceive().ExecuteInTransactionAsync(Arg.Any<Func<Task<PartitionApplyOutcome>>>());
        await _groupItemRepository.DidNotReceive().GetByClientAndGroup(Arg.Any<Guid>(), Arg.Any<Guid>());
    }

    [Test]
    public async Task Apply_CreatesGroupsFirst_ThenLooksUpAndAddsMemberships_CommitsOnce_ThenVerifies()
    {
        SetEmployees(Bern("Anna"), Bern("Ben"));
        var calls = new List<string>();
        var added = new List<Group>();
        await _groupRepository.Add(Arg.Do<Group>(g =>
        {
            calls.Add("Group " + g.Name);
            added.Add(g);
        }));
        _groupItemRepository.GetByClientAndGroup(Arg.Any<Guid>(), Arg.Any<Guid>())
            .Returns(ci =>
            {
                calls.Add("Lookup");
                return (GroupItem?)null;
            });
        await _groupItemRepository.Add(Arg.Do<GroupItem>(_ => calls.Add("Add")));
        _unitOfWork.When(u => u.CompleteAsync()).Do(_ => calls.Add("Complete"));
        _groupItemRepository.CountExistingByIds(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                calls.Add("Count");
                return ci.Arg<IReadOnlyCollection<Guid>>().Count;
            });

        var result = await Handler().Handle(Command(apply: true), CancellationToken.None);

        calls.ShouldBe(new[]
        {
            "Group Deutschschweiz Mitte", "Group BE", "Lookup", "Lookup", "Add", "Add", "Complete", "Count"
        });
        var region = added.Single(g => g.Name == "Deutschschweiz Mitte");
        var state = added.Single(g => g.Name == "BE");
        region.Parent.ShouldBeNull();
        state.Parent.ShouldBe(region.Id);
        state.PaymentInterval.ShouldBe(PaymentInterval.Monthly);
        state.CurrentUserCreated.ShouldBe("tester");
        state.GeocodingAttempted.ShouldBeFalse();
        result.Applied.ShouldBeTrue();
        result.AssignedCount.ShouldBe(2);
        result.VerifiedCount.ShouldBe(2);
        result.AlreadyMemberCount.ShouldBe(0);
        result.UsersKeepingFullVisibilityCount.ShouldBe(3);
        result.Groups.Single(g => g.Name == "BE").GroupId.ShouldBe(state.Id);
        result.Groups.Single(g => g.Name == "Deutschschweiz Mitte").GroupId.ShouldBe(region.Id);
        result.MembershipStart!.PersonCount.ShouldBe(2);
    }

    [Test]
    public async Task Apply_VerificationMismatch_ThrowsTheExactRollbackMessage()
    {
        SetEmployees(Bern("Anna"), Bern("Ben"));
        _groupItemRepository.CountExistingByIds(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(1);

        var ex = await Should.ThrowAsync<SkillVerificationException>(
            () => Handler().Handle(Command(apply: true), CancellationToken.None));

        ex.SkillName.ShouldBe("partition_clients_by_address");
        ex.ErrorCode.ShouldBe("VERIFICATION_FAILED");
        ex.Message.ShouldBe(
            "Database verification failed: expected 2 new memberships but only 1 were confirmed — the changes were rolled back.");
        await _unitOfWork.Received(1).CompleteAsync();
    }
}
