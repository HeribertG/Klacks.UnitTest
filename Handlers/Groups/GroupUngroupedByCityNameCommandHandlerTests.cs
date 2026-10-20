// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for the start of the memberships GroupUngroupedByCityNameCommandHandler creates: a fixed run starts everybody
/// on the given date (else the company today) and reports how many placed employees entered earlier, an entry-date run starts
/// each employee on their own entry date, and the preview describes the start without writing anything.
/// </summary>

using Klacks.Api.Application.Commands.Groups;
using Klacks.Api.Application.Handlers.Groups;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Handlers.Groups;

[TestFixture]
public class GroupUngroupedByCityNameCommandHandlerTests
{
    private static readonly DateTime CompanyToday = new(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime EntryUtc = new(2021, 4, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly Guid BernGroupId = Guid.NewGuid();

    private IClientRepository _clientRepository = null!;
    private IGroupRepository _groupRepository = null!;
    private IGroupItemRepository _groupItemRepository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private ICompanyClock _companyClock = null!;
    private Client _anna = null!;
    private Client _ben = null!;

    [SetUp]
    public void SetUp()
    {
        _clientRepository = Substitute.For<IClientRepository>();
        _groupRepository = Substitute.For<IGroupRepository>();
        _groupItemRepository = Substitute.For<IGroupItemRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _companyClock = Substitute.For<ICompanyClock>();
        _companyClock.GetTodayAsync(Arg.Any<CancellationToken>()).Returns(CompanyToday);

        _anna = Employee("Anna");
        _ben = Employee("Ben");
        _clientRepository.GetByTypeWithAddressesAndGroupItemsAsync(EntityTypeEnum.Employee, Arg.Any<CancellationToken>())
            .Returns(new List<Client> { _anna, _ben });
        _groupRepository.List().Returns(new List<Group> { new() { Id = BernGroupId, Name = "Bern" } });
        _groupItemRepository.GetByClientAndGroup(Arg.Any<Guid>(), Arg.Any<Guid>()).Returns((GroupItem?)null);
        _groupItemRepository.CountExistingByIds(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<IReadOnlyCollection<Guid>>().Count);
        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<int>>>()).Returns(ci => ci.Arg<Func<Task<int>>>()());
    }

    private static Client Employee(string firstName)
    {
        var id = Guid.NewGuid();
        return new Client
        {
            Id = id,
            FirstName = firstName,
            Name = "Muster",
            Type = EntityTypeEnum.Employee,
            Addresses = new List<Address>
            {
                new() { ClientId = id, Type = AddressTypeEnum.Employee, City = "Bern", ValidFrom = CompanyToday }
            }
        };
    }

    private GroupUngroupedByCityNameCommandHandler Handler() => new(
        _clientRepository, _groupRepository, _groupItemRepository, _unitOfWork,
        TestGroupWriteVisibility.UnrestrictedGroups(),
        TestGroupMembershipStart.Planner(_companyClock, (_anna.Id, new DateOnly(2021, 4, 1))));

    private static GroupUngroupedByCityNameCommand Command(
        bool apply, DateTime? validFrom = null, GroupMembershipStartMode mode = GroupMembershipStartMode.Fixed) =>
        new(null, validFrom, apply, "tester", mode);

    [Test]
    public async Task Preview_DescribesTheStart_AndWritesNothing()
    {
        var result = await Handler().Handle(Command(apply: false), CancellationToken.None);

        result.MembershipStart!.StartDate.ShouldBe("2026-10-20");
        result.MembershipStart.StartDateGiven.ShouldBeFalse();
        result.MembershipStart.PersonCount.ShouldBe(2);
        result.MembershipStart.PersonsEnteredBeforeStartCount.ShouldBe(1);
        result.MembershipStart.EarliestEntryDate.ShouldBe("2021-04-01");
        await _groupItemRepository.DidNotReceive().Add(Arg.Any<GroupItem>());
    }

    [Test]
    public async Task Apply_Fixed_StartsEverybodyOnTheGivenDate()
    {
        var given = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);

        var result = await Handler().Handle(Command(apply: true, given), CancellationToken.None);

        await _groupItemRepository.Received(2).Add(Arg.Is<GroupItem>(gi => gi.ValidFrom == given));
        result.MembershipStart!.StartDate.ShouldBe("2026-05-01");
        result.MembershipStart.PersonsEnteredBeforeStartCount.ShouldBe(1);
    }

    [Test]
    public async Task Apply_InEntryDateMode_StartsEachEmployeeOnTheirEntry_AndTheOneWithoutRecordToday()
    {
        var result = await Handler().Handle(
            Command(apply: true, mode: GroupMembershipStartMode.EntryDate), CancellationToken.None);

        await _groupItemRepository.Received(1).Add(Arg.Is<GroupItem>(gi => gi.ClientId == _anna.Id && gi.ValidFrom == EntryUtc));
        await _groupItemRepository.Received(1).Add(Arg.Is<GroupItem>(gi => gi.ClientId == _ben.Id && gi.ValidFrom == CompanyToday));
        result.MembershipStart!.Mode.ShouldBe(GroupMembershipStartMode.EntryDate);
        result.MembershipStart.PersonsWithoutEntryDateCount.ShouldBe(1);
    }

    [Test]
    public async Task Preview_AndApply_CountTheSamePersons()
    {
        var preview = await Handler().Handle(Command(apply: false), CancellationToken.None);
        var applied = await Handler().Handle(Command(apply: true), CancellationToken.None);

        preview.MembershipStart!.PersonCount.ShouldBe(applied.MembershipStart!.PersonCount);
        preview.MembershipStart.PersonsEnteredBeforeStartCount.ShouldBe(applied.MembershipStart.PersonsEnteredBeforeStartCount);
    }

    [Test]
    public async Task Preview_ExcludesAPersonWhoAlreadyHoldsTheMembership_LikeTheApplyDoes()
    {
        _groupItemRepository.GetByClientAndGroup(_ben.Id, BernGroupId)
            .Returns(new GroupItem { Id = Guid.NewGuid(), ClientId = _ben.Id, GroupId = BernGroupId });

        var preview = await Handler().Handle(Command(apply: false), CancellationToken.None);
        var applied = await Handler().Handle(Command(apply: true), CancellationToken.None);

        preview.MembershipStart!.PersonCount.ShouldBe(1);
        applied.MembershipStart!.PersonCount.ShouldBe(1);
    }
}
