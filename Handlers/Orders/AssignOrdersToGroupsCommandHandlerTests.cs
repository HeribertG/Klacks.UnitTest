// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for AssignOrdersToGroupsCommandHandler: the target group is derived from the customer's
/// address in the fixed precedence city name, canton code, nearest coordinates and finally unassigned;
/// the workplace address outranks the main address; a preview never touches a repository or the unit of
/// work; an apply writes one link per order; and an order that already holds a link is skipped, which is
/// the shape a second apply run takes.
/// </summary>

using Klacks.Api.Application.Commands.Orders;
using Klacks.Api.Application.Handlers.Orders;
using Klacks.Api.Domain.DTOs.Filter;

namespace Klacks.UnitTest.Handlers.Orders;

[TestFixture]
public class AssignOrdersToGroupsCommandHandlerTests
{
    private static readonly DateTime CompanyToday = new(2099, 1, 15, 0, 0, 0, DateTimeKind.Utc);

    private IShiftRepository _shiftRepository = null!;
    private IGroupRepository _groupRepository = null!;
    private IGroupItemRepository _groupItemRepository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private ICompanyClock _companyClock = null!;
    private AssignOrdersToGroupsCommandHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        _shiftRepository = Substitute.For<IShiftRepository>();
        _groupRepository = Substitute.For<IGroupRepository>();
        _groupItemRepository = Substitute.For<IGroupItemRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _companyClock = Substitute.For<ICompanyClock>();
        _companyClock.GetTodayAsync(Arg.Any<CancellationToken>()).Returns(CompanyToday);

        _handler = new AssignOrdersToGroupsCommandHandler(
            _shiftRepository, _groupRepository, _groupItemRepository, _unitOfWork, _companyClock);

        _groupRepository.List().Returns(new List<Group>());
        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<int>>>())
            .Returns(ci => ci.Arg<Func<Task<int>>>()());
        _groupItemRepository.CountExistingByIds(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<IReadOnlyCollection<Guid>>().Count);
    }

    private static AssignOrdersToGroupsCommand Command(bool apply) =>
        new(SourceSystemId: null, FromDate: null, UntilDate: null, CustomerName: null,
            MaxCount: null, ValidFrom: null, apply, "tester");

    private static Group NamedGroup(string name, double? latitude = null, double? longitude = null) =>
        new() { Id = Guid.NewGuid(), Name = name, Latitude = latitude, Longitude = longitude };

    private static Shift OrderFor(Client customer, params GroupItem[] groupItems) =>
        new()
        {
            Id = Guid.NewGuid(),
            Name = "ERP order",
            Status = ShiftStatus.OriginalOrder,
            ClientId = customer.Id,
            Client = customer,
            GroupItems = groupItems.ToList()
        };

    private static Client Customer(params Address[] addresses)
    {
        var customerId = Guid.NewGuid();
        foreach (var address in addresses)
        {
            address.ClientId = customerId;
        }

        return new Client
        {
            Id = customerId,
            Name = "Muster AG",
            Company = "Muster AG",
            Type = EntityTypeEnum.Customer,
            Addresses = addresses.ToList()
        };
    }

    private static Address Addr(
        AddressTypeEnum type = AddressTypeEnum.Employee,
        string city = "",
        string state = "",
        double? latitude = null,
        double? longitude = null) =>
        new()
        {
            Type = type,
            City = city,
            State = state,
            Latitude = latitude,
            Longitude = longitude,
            ValidFrom = CompanyToday
        };

    private void OpenOrders(params Shift[] orders) =>
        _shiftRepository.GetOpenOrdersAsync(Arg.Any<OpenOrderFilter>(), Arg.Any<CancellationToken>())
            .Returns(orders.ToList());

    [Test]
    public async Task PlacesOrder_IntoTheGroupNamedAfterTheCustomersCity()
    {
        var bern = NamedGroup("Bern");
        _groupRepository.List().Returns(new List<Group> { bern, NamedGroup("BE") });
        OpenOrders(OrderFor(Customer(Addr(city: "Bern", state: "BE"))));

        var result = await _handler.Handle(Command(apply: false), CancellationToken.None);

        result.AssignedCount.ShouldBe(1);
        result.AssignmentSample[0].GroupId.ShouldBe(bern.Id);
        result.AssignmentSample[0].MatchReason.ShouldContain("city name");
    }

    [Test]
    public async Task FallsBackToTheCantonCode_WhenNoGroupCarriesTheCityName()
    {
        var canton = NamedGroup("BE");
        _groupRepository.List().Returns(new List<Group> { canton });
        OpenOrders(OrderFor(Customer(Addr(city: "Worb", state: "BE"))));

        var result = await _handler.Handle(Command(apply: false), CancellationToken.None);

        result.AssignedCount.ShouldBe(1);
        result.AssignmentSample[0].GroupId.ShouldBe(canton.Id);
        result.AssignmentSample[0].MatchReason.ShouldContain("canton code");
    }

    [Test]
    public async Task FallsBackToTheNearestGroupWithCoordinates_WhenNoNameMatches()
    {
        var far = NamedGroup("Zone North", 47.5, 7.6);
        var near = NamedGroup("Zone South", 46.95, 7.45);
        _groupRepository.List().Returns(new List<Group> { far, near });
        OpenOrders(OrderFor(Customer(Addr(city: "Worb", state: "BE", latitude: 46.93, longitude: 7.44))));

        var result = await _handler.Handle(Command(apply: false), CancellationToken.None);

        result.AssignedCount.ShouldBe(1);
        result.AssignmentSample[0].GroupId.ShouldBe(near.Id);
        result.AssignmentSample[0].MatchReason.ShouldContain("nearest coordinates");
        result.AssignmentSample[0].DistanceKm.ShouldNotBeNull();
    }

    [Test]
    public async Task ReportsUnassignable_WhenNoNameMatchesAndNoGroupCarriesCoordinates()
    {
        _groupRepository.List().Returns(new List<Group> { NamedGroup("Zurich") });
        OpenOrders(OrderFor(Customer(Addr(city: "Worb", state: "BE"))));

        var result = await _handler.Handle(Command(apply: false), CancellationToken.None);

        result.AssignedCount.ShouldBe(0);
        result.UnassignableCount.ShouldBe(1);
        result.UnassignableSample[0].Reason.ShouldContain("no group");
    }

    [Test]
    public async Task ReportsUnassignable_WhenTheOrderHasNoCustomer()
    {
        _groupRepository.List().Returns(new List<Group> { NamedGroup("Bern") });
        OpenOrders(new Shift { Id = Guid.NewGuid(), Name = "orphan order", Status = ShiftStatus.OriginalOrder });

        var result = await _handler.Handle(Command(apply: false), CancellationToken.None);

        result.UnassignableCount.ShouldBe(1);
        result.UnassignableSample[0].Reason.ShouldContain("no customer");
    }

    [Test]
    public async Task PrefersTheWorkplaceAddress_OverTheMainAddress()
    {
        var workplaceCity = NamedGroup("Thun");
        _groupRepository.List().Returns(new List<Group> { NamedGroup("Bern"), workplaceCity });
        OpenOrders(OrderFor(Customer(
            Addr(AddressTypeEnum.Employee, city: "Bern", state: "BE"),
            Addr(AddressTypeEnum.Workplace, city: "Thun", state: "BE"))));

        var result = await _handler.Handle(Command(apply: false), CancellationToken.None);

        result.AssignmentSample[0].GroupId.ShouldBe(workplaceCity.Id);
        result.AssignmentSample[0].MatchReason.ShouldContain("workplace address");
    }

    [Test]
    public async Task Preview_DoesNotPersistAnything()
    {
        _groupRepository.List().Returns(new List<Group> { NamedGroup("Bern") });
        OpenOrders(OrderFor(Customer(Addr(city: "Bern", state: "BE"))));

        var result = await _handler.Handle(Command(apply: false), CancellationToken.None);

        result.Applied.ShouldBeFalse();
        result.VerifiedCount.ShouldBe(0);
        await _groupItemRepository.DidNotReceive().Add(Arg.Any<GroupItem>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Apply_WritesOneLinkPerOrder_AndConfirmsThem()
    {
        var bern = NamedGroup("Bern");
        _groupRepository.List().Returns(new List<Group> { bern });
        OpenOrders(
            OrderFor(Customer(Addr(city: "Bern", state: "BE"))),
            OrderFor(Customer(Addr(city: "Bern", state: "BE"))));

        var result = await _handler.Handle(Command(apply: true), CancellationToken.None);

        result.Applied.ShouldBeTrue();
        result.AssignedCount.ShouldBe(2);
        result.VerifiedCount.ShouldBe(2);
        await _groupItemRepository.Received(2).Add(Arg.Is<GroupItem>(gi =>
            gi.GroupId == bern.Id && gi.ShiftId != null && gi.ClientId == null));
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task SecondApplyRun_ChangesNothing_BecauseTheOrdersAlreadyHoldALink()
    {
        var bern = NamedGroup("Bern");
        _groupRepository.List().Returns(new List<Group> { bern });
        var linkedOrder = OrderFor(
            Customer(Addr(city: "Bern", state: "BE")),
            new GroupItem { Id = Guid.NewGuid(), GroupId = bern.Id });
        OpenOrders(linkedOrder);

        var result = await _handler.Handle(Command(apply: true), CancellationToken.None);

        result.AssignedCount.ShouldBe(0);
        result.SkippedAlreadyGroupedCount.ShouldBe(1);
        await _groupItemRepository.DidNotReceive().Add(Arg.Any<GroupItem>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task ScenarioMembership_DoesNotCountAsAnExistingLink()
    {
        var bern = NamedGroup("Bern");
        _groupRepository.List().Returns(new List<Group> { bern });
        OpenOrders(OrderFor(
            Customer(Addr(city: "Bern", state: "BE")),
            new GroupItem { Id = Guid.NewGuid(), GroupId = bern.Id, AnalyseToken = Guid.NewGuid() }));

        var result = await _handler.Handle(Command(apply: false), CancellationToken.None);

        result.SkippedAlreadyGroupedCount.ShouldBe(0);
        result.AssignedCount.ShouldBe(1);
    }
}
