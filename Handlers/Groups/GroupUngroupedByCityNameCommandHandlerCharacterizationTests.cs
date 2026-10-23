// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Characterization tests for GroupUngroupedByCityNameCommandHandler.Handle: the restricted-caller refusal, which
/// employees count as ungrouped, the city match against unique non-deleted group names, the no-address and
/// unmatched-city reporting (order and spelling), the Count limit, the preview/apply result shape, the skipped
/// transaction when nothing is new, the write order inside the transaction and the exact verification failure text.
/// </summary>

using Klacks.Api.Application.Commands.Groups;
using Klacks.Api.Application.DTOs.Groups;
using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Handlers.Groups;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Handlers.Groups;

[TestFixture]
public class GroupUngroupedByCityNameCommandHandlerCharacterizationTests
{
    private static readonly DateTime CompanyToday = new(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime OlderAddress = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private IClientRepository _clientRepository = null!;
    private IGroupRepository _groupRepository = null!;
    private IGroupItemRepository _groupItemRepository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private ICompanyClock _companyClock = null!;
    private Guid _bernGroupId;

    [SetUp]
    public void SetUp()
    {
        _clientRepository = Substitute.For<IClientRepository>();
        _groupRepository = Substitute.For<IGroupRepository>();
        _groupItemRepository = Substitute.For<IGroupItemRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _companyClock = Substitute.For<ICompanyClock>();
        _companyClock.GetTodayAsync(Arg.Any<CancellationToken>()).Returns(CompanyToday);
        _bernGroupId = Guid.NewGuid();

        _groupRepository.List().Returns(new List<Group>
        {
            new() { Id = _bernGroupId, Name = "Bern" },
            new() { Id = Guid.NewGuid(), Name = "Thun" },
            new() { Id = Guid.NewGuid(), Name = " thun " },
            new() { Id = Guid.NewGuid(), Name = "Biel", IsDeleted = true },
            new() { Id = Guid.NewGuid(), Name = "   " }
        });
        _groupItemRepository.GetByClientAndGroup(Arg.Any<Guid>(), Arg.Any<Guid>()).Returns((GroupItem?)null);
        _groupItemRepository.CountExistingByIds(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<IReadOnlyCollection<Guid>>().Count);
        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<int>>>()).Returns(ci => ci.Arg<Func<Task<int>>>()());
    }

    private GroupUngroupedByCityNameCommandHandler Handler() => new(
        _clientRepository, _groupRepository, _groupItemRepository, _unitOfWork,
        TestGroupWriteVisibility.UnrestrictedGroups(),
        TestGroupMembershipStart.Planner(_companyClock));

    private static GroupUngroupedByCityNameCommand Command(bool apply, int? count = null) =>
        new(count, null, apply, "tester");

    private void SetEmployees(params Client[] employees) =>
        _clientRepository.GetByTypeWithAddressesAndGroupItemsAsync(EntityTypeEnum.Employee, Arg.Any<CancellationToken>())
            .Returns(employees.ToList());

    private static Client Employee(string firstName, params Address[] addresses)
    {
        var id = Guid.NewGuid();
        foreach (var address in addresses)
        {
            address.ClientId = id;
        }

        return new Client
        {
            Id = id,
            FirstName = firstName,
            Name = "Muster",
            Type = EntityTypeEnum.Employee,
            Addresses = addresses.ToList()
        };
    }

    private static Address At(string city, DateTime? validFrom = null, AddressTypeEnum type = AddressTypeEnum.Employee) =>
        new() { Type = type, City = city, ValidFrom = validFrom ?? CompanyToday };

    [Test]
    public async Task RestrictedCaller_IsRefusedWithTheExactMessage_BeforeAnythingIsRead()
    {
        var guard = Substitute.For<IGroupVisibilityGuard>();
        guard.IsUnrestrictedAsync(Arg.Any<CancellationToken>()).Returns(false);
        var handler = new GroupUngroupedByCityNameCommandHandler(
            _clientRepository, _groupRepository, _groupItemRepository, _unitOfWork,
            guard, TestGroupMembershipStart.Planner(_companyClock));

        var ex = await Should.ThrowAsync<ForbiddenException>(() => handler.Handle(Command(apply: true), CancellationToken.None));

        ex.Message.ShouldBe("Only unrestricted users may group employees across all groups");
        await _clientRepository.DidNotReceive()
            .GetByTypeWithAddressesAndGroupItemsAsync(Arg.Any<EntityTypeEnum>(), Arg.Any<CancellationToken>());
        await _groupRepository.DidNotReceive().List();
    }

    [Test]
    public async Task Preview_ReportsUngroupedMatchesNoAddressAndUnmatchedCities_AndWritesNothing()
    {
        var anna = Employee("Anna", At("Thun", OlderAddress), At(" Bern "));
        var grouped = Employee("Gabi", At("Bern"));
        grouped.GroupItems.Add(new GroupItem { Id = Guid.NewGuid(), ClientId = grouped.Id, GroupId = Guid.NewGuid() });
        var scenarioOnly = Employee("Sara", At("bern"));
        scenarioOnly.GroupItems.Add(new GroupItem
        {
            Id = Guid.NewGuid(), ClientId = scenarioOnly.Id, GroupId = Guid.NewGuid(), AnalyseToken = Guid.NewGuid()
        });
        var deletedOnly = Employee("Dora");
        deletedOnly.GroupItems.Add(new GroupItem { Id = Guid.NewGuid(), ClientId = deletedOnly.Id, GroupId = Guid.NewGuid(), IsDeleted = true });
        var removedAddress = At("Bern");
        removedAddress.IsDeleted = true;
        var withRemovedAddress = Employee("Rolf", removedAddress);
        var workplaceOnly = Employee("Will", At("Bern", type: AddressTypeEnum.Workplace));
        var thun1 = Employee("Tim", At("Thun"));
        var thun2 = Employee("Tom", At("THUN"));
        var zug1 = Employee("Zoe", At("zug"));
        var zug2 = Employee("Zack", At(" Zug "));
        var biel = Employee("Bea", At("Biel"));
        SetEmployees(anna, grouped, scenarioOnly, deletedOnly, withRemovedAddress, workplaceOnly, thun1, thun2, zug1, zug2, biel);

        var result = await Handler().Handle(Command(apply: false), CancellationToken.None);

        result.Applied.ShouldBeFalse();
        result.TotalUngrouped.ShouldBe(10);
        result.MatchCount.ShouldBe(2);
        result.AddedCount.ShouldBe(0);
        result.VerifiedCount.ShouldBe(0);
        result.NoAddressCount.ShouldBe(3);
        result.Assignments.ShouldBe(new[]
        {
            new GroupCityAssignment(anna.Id, "Anna Muster", "Bern", _bernGroupId, "Bern"),
            new GroupCityAssignment(scenarioOnly.Id, "Sara Muster", "bern", _bernGroupId, "Bern")
        });
        result.UnmatchedCities.ShouldBe(new[]
        {
            new UnmatchedCityCount("Thun", 2),
            new UnmatchedCityCount("zug", 2),
            new UnmatchedCityCount("Biel", 1)
        });
        await _groupItemRepository.Received(2).GetByClientAndGroup(Arg.Any<Guid>(), Arg.Is(_bernGroupId));
        await _groupItemRepository.DidNotReceive().Add(Arg.Any<GroupItem>());
        await _unitOfWork.DidNotReceive().ExecuteInTransactionAsync(Arg.Any<Func<Task<int>>>());
    }

    [Test]
    public async Task Count_LimitsTheAssignmentsToTheFirstMatches_ButNotTheUngroupedTotal()
    {
        var first = Employee("Anna", At("Bern"));
        var second = Employee("Ben", At("Bern"));
        SetEmployees(first, second);

        var result = await Handler().Handle(Command(apply: true, count: 1), CancellationToken.None);

        result.TotalUngrouped.ShouldBe(2);
        result.MatchCount.ShouldBe(1);
        result.Assignments.ShouldHaveSingleItem().ClientId.ShouldBe(first.Id);
        result.AddedCount.ShouldBe(1);
        await _groupItemRepository.Received(1).Add(Arg.Is<GroupItem>(gi => gi.ClientId == first.Id));
    }

    [Test]
    public async Task CountZero_DoesNotLimit()
    {
        SetEmployees(Employee("Anna", At("Bern")), Employee("Ben", At("Bern")));

        var result = await Handler().Handle(Command(apply: false, count: 0), CancellationToken.None);

        result.MatchCount.ShouldBe(2);
    }

    [Test]
    public async Task Apply_WritesTheNewMemberships_CommitsOnce_AndVerifiesAfterTheCommit()
    {
        var anna = Employee("Anna", At("Bern"));
        var ben = Employee("Ben", At("Bern"));
        SetEmployees(anna, ben);
        var calls = new List<string>();
        _groupItemRepository.GetByClientAndGroup(Arg.Any<Guid>(), Arg.Any<Guid>())
            .Returns(ci =>
            {
                calls.Add("Lookup");
                return (GroupItem?)null;
            });
        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<int>>>())
            .Returns(ci =>
            {
                calls.Add("Transaction");
                return ci.Arg<Func<Task<int>>>()();
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

        result.Applied.ShouldBeTrue();
        result.TotalUngrouped.ShouldBe(2);
        result.MatchCount.ShouldBe(2);
        result.AddedCount.ShouldBe(2);
        result.VerifiedCount.ShouldBe(2);
        result.NoAddressCount.ShouldBe(0);
        result.UnmatchedCities.ShouldBeEmpty();
        await _groupItemRepository.Received(2).Add(Arg.Is<GroupItem>(gi =>
            gi.GroupId == _bernGroupId && gi.CurrentUserCreated == "tester" && gi.ValidFrom == CompanyToday && gi.Id != Guid.Empty));
        await _groupItemRepository.Received(1).Add(Arg.Is<GroupItem>(gi => gi.ClientId == anna.Id));
        await _groupItemRepository.Received(1).Add(Arg.Is<GroupItem>(gi => gi.ClientId == ben.Id));
        await _unitOfWork.Received(1).CompleteAsync();
        calls.ShouldBe(new[] { "Lookup", "Lookup", "Transaction", "Add", "Add", "Complete", "Count" });
    }

    [Test]
    public async Task Apply_WhenEveryMatchIsAlreadyAMember_SkipsTheTransaction()
    {
        var anna = Employee("Anna", At("Bern"));
        SetEmployees(anna);
        _groupItemRepository.GetByClientAndGroup(anna.Id, _bernGroupId)
            .Returns(new GroupItem { Id = Guid.NewGuid(), ClientId = anna.Id, GroupId = _bernGroupId, ValidFrom = CompanyToday });

        var result = await Handler().Handle(Command(apply: true), CancellationToken.None);

        result.Applied.ShouldBeTrue();
        result.MatchCount.ShouldBe(1);
        result.AddedCount.ShouldBe(0);
        result.VerifiedCount.ShouldBe(0);
        await _unitOfWork.DidNotReceive().ExecuteInTransactionAsync(Arg.Any<Func<Task<int>>>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
        await _groupItemRepository.DidNotReceive().Add(Arg.Any<GroupItem>());
    }

    [Test]
    public async Task Apply_ADeletedExistingMembership_CountsAsNew()
    {
        var anna = Employee("Anna", At("Bern"));
        SetEmployees(anna);
        _groupItemRepository.GetByClientAndGroup(anna.Id, _bernGroupId)
            .Returns(new GroupItem { Id = Guid.NewGuid(), ClientId = anna.Id, GroupId = _bernGroupId, IsDeleted = true });

        var result = await Handler().Handle(Command(apply: true), CancellationToken.None);

        result.AddedCount.ShouldBe(1);
        result.VerifiedCount.ShouldBe(1);
        await _groupItemRepository.Received(1).Add(Arg.Is<GroupItem>(gi => gi.ClientId == anna.Id));
    }

    [Test]
    public async Task Apply_VerificationMismatch_ThrowsTheExactRollbackMessage()
    {
        SetEmployees(Employee("Anna", At("Bern")), Employee("Ben", At("Bern")));
        _groupItemRepository.CountExistingByIds(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(1);

        var ex = await Should.ThrowAsync<SkillVerificationException>(
            () => Handler().Handle(Command(apply: true), CancellationToken.None));

        ex.SkillName.ShouldBe("group_ungrouped_by_city_name");
        ex.ErrorCode.ShouldBe("VERIFICATION_FAILED");
        ex.Message.ShouldBe(
            "Database verification failed: expected 2 new memberships but only 1 were confirmed — the changes were rolled back.");
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task GroupName_IsReportedUntrimmed_WhileTheCityIsTrimmed()
    {
        var spacedId = Guid.NewGuid();
        _groupRepository.List().Returns(new List<Group> { new() { Id = spacedId, Name = " Olten " } });
        var olga = Employee("Olga", At("  olten "));
        SetEmployees(olga);

        var result = await Handler().Handle(Command(apply: false), CancellationToken.None);

        result.Assignments.ShouldHaveSingleItem().ShouldBe(new GroupCityAssignment(olga.Id, "Olga Muster", "olten", spacedId, " Olten "));
    }
}
