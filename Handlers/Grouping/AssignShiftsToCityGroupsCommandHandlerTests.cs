// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for AssignShiftsToCityGroupsCommandHandler: a preview writes nothing, an apply removes every
/// replaced link and adds one city-group link per planned shift, and a database state that does not match
/// the plan after the commit raises a verification error so the transaction rolls back.
/// </summary>

using Klacks.Api.Application.Commands.Grouping;
using Klacks.Api.Application.DTOs.Grouping;
using Klacks.Api.Application.Handlers.Grouping;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Domain.Exceptions;

namespace Klacks.UnitTest.Handlers.Grouping;

[TestFixture]
public class AssignShiftsToCityGroupsCommandHandlerTests
{
    private static readonly DateTime CompanyToday = new(2099, 1, 15, 0, 0, 0, DateTimeKind.Utc);

    private IShiftRepository _shiftRepository = null!;
    private IGroupRepository _groupRepository = null!;
    private IAddressRepository _addressRepository = null!;
    private IGroupItemRepository _groupItemRepository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private ICompanyClock _companyClock = null!;
    private AssignShiftsToCityGroupsCommandHandler _handler = null!;

    private Group _region = null!;
    private Group _city = null!;
    private GroupItem _regionLink = null!;

    [SetUp]
    public void Setup()
    {
        _shiftRepository = Substitute.For<IShiftRepository>();
        _groupRepository = Substitute.For<IGroupRepository>();
        _addressRepository = Substitute.For<IAddressRepository>();
        _groupItemRepository = Substitute.For<IGroupItemRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _companyClock = Substitute.For<ICompanyClock>();
        _companyClock.GetTodayAsync(Arg.Any<CancellationToken>()).Returns(CompanyToday);

        _handler = new AssignShiftsToCityGroupsCommandHandler(
            _shiftRepository, _groupRepository, _addressRepository, _groupItemRepository, _unitOfWork, _companyClock);

        var regionId = Guid.NewGuid();
        _region = new Group { Id = regionId, Name = "Region", Root = regionId, Lft = 1, Rgt = 4 };
        _city = new Group { Id = Guid.NewGuid(), Name = "Bern", Parent = regionId, Root = regionId, Lft = 2, Rgt = 3 };
        _regionLink = new GroupItem { Id = Guid.NewGuid(), GroupId = _region.Id };

        _groupRepository.List().Returns(new List<Group> { _region, _city });
        _addressRepository.GetCityCentroidsAsync(Arg.Any<CancellationToken>()).Returns(new List<CityCentroid>());
        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<int>>>())
            .Returns(ci => ci.Arg<Func<Task<int>>>()());

        var customerId = Guid.NewGuid();
        var customer = new Client
        {
            Id = customerId,
            Name = "Muster AG",
            Type = EntityTypeEnum.Customer,
            Addresses = [new Address { ClientId = customerId, City = "Bern", State = "BE" }]
        };
        var shift = new Shift
        {
            Id = Guid.NewGuid(),
            Name = "Service",
            Status = ShiftStatus.SealedOrder,
            ClientId = customerId,
            Client = customer,
            GroupItems = [_regionLink]
        };
        _shiftRepository.GetShiftsForCityGroupPlacementAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new List<Shift> { shift });
    }

    private void DatabaseReports(int newLinks, int oldLinks) =>
        _groupItemRepository.CountExistingByIds(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<IReadOnlyCollection<Guid>>().Contains(_regionLink.Id) ? oldLinks : newLinks);

    private static AssignShiftsToCityGroupsCommand Command(bool apply) => new(null, null, apply, "tester");

    [Test]
    public async Task Preview_WritesNothing()
    {
        var result = await _handler.Handle(Command(apply: false), CancellationToken.None);

        result.Applied.ShouldBeFalse();
        result.AssignedCount.ShouldBe(1);
        result.ReplacedLinkCount.ShouldBe(1);
        await _groupItemRepository.DidNotReceiveWithAnyArgs().Add(default!);
        await _groupItemRepository.DidNotReceiveWithAnyArgs().RemoveByIdsAsync(default!, default);
        await _unitOfWork.DidNotReceiveWithAnyArgs().CompleteAsync();
    }

    [Test]
    public async Task Apply_ReplacesTheRegionLinkWithTheCityLink()
    {
        DatabaseReports(newLinks: 1, oldLinks: 0);

        var result = await _handler.Handle(Command(apply: true), CancellationToken.None);

        result.Applied.ShouldBeTrue();
        result.VerifiedCount.ShouldBe(1);
        await _groupItemRepository.Received(1).RemoveByIdsAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Single() == _regionLink.Id), Arg.Any<CancellationToken>());
        await _groupItemRepository.Received(1).Add(Arg.Is<GroupItem>(gi => gi.GroupId == _city.Id));
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task Apply_Throws_WhenTheReplacedLinkIsStillActiveAfterTheCommit()
    {
        DatabaseReports(newLinks: 1, oldLinks: 1);

        await Should.ThrowAsync<SkillVerificationException>(
            () => _handler.Handle(Command(apply: true), CancellationToken.None));
    }
}
