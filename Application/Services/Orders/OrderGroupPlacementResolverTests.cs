// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for OrderGroupPlacementResolver, the group seal_shift places an order without a group in: it loads exactly
/// that open order the way assign_orders_to_groups loads its batch (GetOpenOrdersAsync, now filtered by the order id, so
/// the customer's addresses and memberships come along) and plans it with OrderGroupPlanner against all groups, so both
/// skills derive the same group. A derived group the caller may not see counts as none found and is not named; an order
/// that is not open (or not found) and an order the planner cannot place yield the reason instead of a group.
/// </summary>

using Klacks.Api.Application.Services.Orders;

namespace Klacks.UnitTest.Application.Services.Orders;

[TestFixture]
public class OrderGroupPlacementResolverTests
{
    private static readonly DateTime CompanyToday = new(2099, 1, 15, 0, 0, 0, DateTimeKind.Utc);

    private IShiftRepository _shiftRepository = null!;
    private IGroupRepository _groupRepository = null!;
    private IGroupVisibilityGuard _groupVisibility = null!;
    private OrderGroupPlacementResolver _resolver = null!;
    private Group _bern = null!;

    [SetUp]
    public void SetUp()
    {
        _shiftRepository = Substitute.For<IShiftRepository>();
        _groupRepository = Substitute.For<IGroupRepository>();
        _groupVisibility = Substitute.For<IGroupVisibilityGuard>();
        _groupVisibility.IsGroupVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
        var companyClock = Substitute.For<ICompanyClock>();
        companyClock.GetTodayAsync(Arg.Any<CancellationToken>()).Returns(CompanyToday);

        _bern = new Group { Id = Guid.NewGuid(), Name = "Bern" };
        _groupRepository.List().Returns(new List<Group> { _bern, new() { Id = Guid.NewGuid(), Name = "Biel" } });

        _resolver = new OrderGroupPlacementResolver(_shiftRepository, _groupRepository, companyClock, _groupVisibility);
    }

    private static Shift OrderInCity(string city)
    {
        var customerId = Guid.NewGuid();
        var customer = new Client
        {
            Id = customerId,
            Name = "Weiss AG",
            Company = "Weiss AG",
            Type = EntityTypeEnum.Customer,
            Addresses = [new Address { ClientId = customerId, Type = AddressTypeEnum.Employee, City = city, ValidFrom = CompanyToday }]
        };

        return new Shift
        {
            Id = Guid.NewGuid(),
            Name = "Nachtdienst Weiss",
            Status = ShiftStatus.OriginalOrder,
            ClientId = customerId,
            Client = customer,
            GroupItems = []
        };
    }

    private void OpenOrder(Shift? order) =>
        _shiftRepository.GetOpenOrdersAsync(Arg.Any<OpenOrderFilter>(), Arg.Any<CancellationToken>())
            .Returns(order == null ? new List<Shift>() : new List<Shift> { order });

    [Test]
    public async Task AnOrderInACity_IsPlacedInTheGroupOfThatCity_LoadedByItsId()
    {
        var order = OrderInCity("Bern");
        OpenOrder(order);

        var placement = await _resolver.ResolveAsync(order.Id);

        placement.Assignment.ShouldNotBeNull();
        placement.Assignment!.GroupId.ShouldBe(_bern.Id);
        placement.Assignment.GroupName.ShouldBe("Bern");
        placement.UnplacedReason.ShouldBeNull();
        await _shiftRepository.Received(1).GetOpenOrdersAsync(
            Arg.Is<OpenOrderFilter>(f => f.OrderId == order.Id), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AGroupTheCallerMayNotSee_CountsAsNoneFound_AndIsNotNamed()
    {
        var order = OrderInCity("Bern");
        OpenOrder(order);
        _groupVisibility.IsGroupVisibleAsync(_bern.Id, Arg.Any<CancellationToken>()).Returns(false);

        var placement = await _resolver.ResolveAsync(order.Id);

        placement.Assignment.ShouldBeNull();
        placement.UnplacedReason.ShouldNotBeNullOrWhiteSpace();
        placement.UnplacedReason!.ShouldNotContain("Bern");
    }

    [Test]
    public async Task AnOrderThePlannerCannotPlace_YieldsThePlannersReason()
    {
        var order = OrderInCity("Thun");
        OpenOrder(order);

        var placement = await _resolver.ResolveAsync(order.Id);

        placement.Assignment.ShouldBeNull();
        placement.UnplacedReason.ShouldNotBeNullOrWhiteSpace();
    }

    [Test]
    public async Task AnOrderThatIsNotOpen_YieldsAReason()
    {
        OpenOrder(null);

        var placement = await _resolver.ResolveAsync(Guid.NewGuid());

        placement.Assignment.ShouldBeNull();
        placement.UnplacedReason.ShouldNotBeNullOrWhiteSpace();
    }
}
