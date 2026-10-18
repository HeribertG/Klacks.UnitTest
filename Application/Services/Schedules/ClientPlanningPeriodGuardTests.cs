// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for ClientPlanningPeriodGuard: the server-side mirror of the schedule grid's locked cells. A day outside
/// the membership of the employee (inclusive bounds, an open end allows everything later) or outside the group period
/// of the groups the grid would show is refused with the reason; an employee without any membership row or group bounds
/// is not blocked, exactly like the grid; the group bounds are the minimum ValidFrom and the maximum ValidUntil of the
/// employee's items in the visible groups, and the visible groups are the selected group of the request or, without
/// one, what the grid would use.
/// </summary>

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Domain.Services.ShiftSchedule;

namespace Klacks.UnitTest.Application.Services.Schedules;

[TestFixture]
public class ClientPlanningPeriodGuardTests
{
    private static readonly Guid ClientId = Guid.NewGuid();
    private static readonly Guid OtherClientId = Guid.NewGuid();
    private static readonly Guid GroupId = Guid.NewGuid();
    private static readonly DateOnly Day = new(2026, 10, 14);

    private IClientRepository _clientRepository = null!;
    private IGroupItemRepository _groupItemRepository = null!;
    private ISelectedGroupContextResolver _groupContextResolver = null!;
    private IShiftGroupFilterService _groupFilterService = null!;
    private ClientPlanningPeriodGuard _guard = null!;

    [SetUp]
    public void SetUp()
    {
        _clientRepository = Substitute.For<IClientRepository>();
        _groupItemRepository = Substitute.For<IGroupItemRepository>();
        _groupContextResolver = Substitute.For<ISelectedGroupContextResolver>();
        _groupFilterService = Substitute.For<IShiftGroupFilterService>();

        _groupContextResolver.ResolveVisibleGroupIdsAsync().Returns((List<Guid>?)null);
        _groupFilterService.GetVisibleGroupIdsAsync(null).Returns(new List<Guid>());
        GivenClients(NewClient(ClientId, membership: null));
        _groupItemRepository
            .GetValidityRangesAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, (DateTime? ValidFrom, DateTime? ValidUntil)>());

        _guard = new ClientPlanningPeriodGuard(
            _clientRepository, _groupItemRepository, _groupContextResolver, _groupFilterService);
    }

    private static Client NewClient(Guid id, Membership? membership) => new()
    {
        Id = id,
        FirstName = "Anna",
        Name = "Meier",
        Membership = membership
    };

    private static Membership NewMembership(DateOnly from, DateOnly? until) => new()
    {
        ValidFrom = from.ToDateTime(TimeOnly.MinValue),
        ValidUntil = until?.ToDateTime(TimeOnly.MinValue)
    };

    private void GivenClients(params Client[] clients)
    {
        _clientRepository.GetByIdsAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(clients.ToList());
    }

    private void GivenGroupRange(Guid clientId, DateOnly? from, DateOnly? until)
    {
        _groupItemRepository
            .GetValidityRangesAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, (DateTime? ValidFrom, DateTime? ValidUntil)>
            {
                [clientId] = (from?.ToDateTime(TimeOnly.MinValue), until?.ToDateTime(TimeOnly.MinValue))
            });
    }

    private Task EnsureAsync(params (Guid ClientId, DateOnly Date)[] cells)
        => _guard.EnsureWithinPeriodsAsync(cells, CancellationToken.None);

    [Test]
    public async Task EmployeeWithoutMembershipRowOrGroupBounds_IsNotBlocked_LikeTheGrid()
    {
        await EnsureAsync((ClientId, Day));
    }

    [Test]
    public async Task DayBeforeTheMembershipStarts_IsRefused_NamingTheMembershipAndTheDay()
    {
        GivenClients(NewClient(ClientId, NewMembership(Day.AddDays(1), null)));

        var ex = await Should.ThrowAsync<InvalidRequestException>(() => EnsureAsync((ClientId, Day)));

        ex.Message.ShouldContain("membership");
        ex.Message.ShouldContain("2026-10-14");
        ex.Message.ShouldContain("Anna Meier");
    }

    [Test]
    public async Task DayAfterTheMembershipEnded_IsRefused()
    {
        GivenClients(NewClient(ClientId, NewMembership(Day.AddDays(-30), Day.AddDays(-1))));

        var ex = await Should.ThrowAsync<InvalidRequestException>(() => EnsureAsync((ClientId, Day)));

        ex.Message.ShouldContain("membership");
    }

    [Test]
    public async Task TheMembershipBoundaryDays_AreStillAllowed()
    {
        GivenClients(NewClient(ClientId, NewMembership(Day, Day.AddDays(5))));

        await EnsureAsync((ClientId, Day));
        await EnsureAsync((ClientId, Day.AddDays(5)));
    }

    [Test]
    public async Task OpenEndedMembership_AllowsEveryLaterDay()
    {
        GivenClients(NewClient(ClientId, NewMembership(Day.AddDays(-10), null)));

        await EnsureAsync((ClientId, Day.AddYears(3)));
    }

    [Test]
    public async Task DayOutsideTheGroupPeriod_IsRefused_NamingTheGroupPeriod()
    {
        _groupContextResolver.ResolveVisibleGroupIdsAsync().Returns(new List<Guid> { GroupId });
        GivenGroupRange(ClientId, Day.AddDays(2), Day.AddDays(9));

        var ex = await Should.ThrowAsync<InvalidRequestException>(() => EnsureAsync((ClientId, Day)));

        ex.Message.ShouldContain("group");
        ex.Message.ShouldContain("2026-10-14");
    }

    [Test]
    public async Task DayAfterTheGroupPeriodEnded_IsRefused()
    {
        _groupContextResolver.ResolveVisibleGroupIdsAsync().Returns(new List<Guid> { GroupId });
        GivenGroupRange(ClientId, Day.AddDays(-9), Day.AddDays(-1));

        await Should.ThrowAsync<InvalidRequestException>(() => EnsureAsync((ClientId, Day)));
    }

    [Test]
    public async Task DayInsideTheGroupPeriod_IsAllowed_AndAHalfOpenPeriodOnlyBoundsOneSide()
    {
        _groupContextResolver.ResolveVisibleGroupIdsAsync().Returns(new List<Guid> { GroupId });
        GivenGroupRange(ClientId, Day.AddDays(-3), null);

        await EnsureAsync((ClientId, Day));
        await EnsureAsync((ClientId, Day.AddYears(2)));
        await Should.ThrowAsync<InvalidRequestException>(() => EnsureAsync((ClientId, Day.AddDays(-4))));
    }

    [Test]
    public async Task TheGroupBoundsAreReadForTheGroupsOfTheSelectedGroupContext()
    {
        var selected = new List<Guid> { GroupId };
        _groupContextResolver.ResolveVisibleGroupIdsAsync().Returns(selected);

        await EnsureAsync((ClientId, Day));

        await _groupItemRepository.Received(1).GetValidityRangesAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Single() == ClientId),
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Single() == GroupId),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task WithoutASelectedGroup_TheGroupsTheGridWouldUseAreRead()
    {
        _groupFilterService.GetVisibleGroupIdsAsync(null).Returns(new List<Guid> { GroupId });

        await EnsureAsync((ClientId, Day));

        await _groupItemRepository.Received(1).GetValidityRangesAsync(
            Arg.Any<IReadOnlyCollection<Guid>>(),
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Single() == GroupId),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task WithoutAnyVisibleGroup_NoGroupPeriodIsConsulted()
    {
        await EnsureAsync((ClientId, Day));

        await _groupItemRepository.DidNotReceiveWithAnyArgs().GetValidityRangesAsync(
            Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SeveralCells_AreChecked_AndTheEarliestViolatingDayIsReported()
    {
        GivenClients(
            NewClient(ClientId, NewMembership(Day.AddDays(-30), null)),
            NewClient(OtherClientId, NewMembership(Day.AddDays(10), null)));

        var ex = await Should.ThrowAsync<InvalidRequestException>(() => EnsureAsync(
            (ClientId, Day),
            (OtherClientId, Day.AddDays(3)),
            (OtherClientId, Day.AddDays(1))));

        ex.Message.ShouldContain("2026-10-15");
    }

    [Test]
    public async Task FindViolations_ListsEveryOffendingCellEarliestFirst_OncePerCell_WithoutThrowing()
    {
        GivenClients(
            NewClient(ClientId, NewMembership(Day.AddDays(-30), null)),
            NewClient(OtherClientId, NewMembership(Day.AddDays(10), null)));

        var violations = await _guard.FindViolationsAsync(
            [(ClientId, Day), (OtherClientId, Day.AddDays(3)), (OtherClientId, Day.AddDays(1)), (OtherClientId, Day.AddDays(1))],
            CancellationToken.None);

        violations.Select(v => (v.ClientId, v.Date)).ShouldBe(
            [(OtherClientId, Day.AddDays(1)), (OtherClientId, Day.AddDays(3))]);
        violations[0].Message.ShouldContain("membership");
        violations[0].Message.ShouldContain("2026-10-15");
    }

    [Test]
    public async Task FindViolations_MembershipReasonWinsOverTheGroupPeriodForTheSameCell()
    {
        GivenClients(NewClient(ClientId, NewMembership(Day.AddDays(1), null)));
        _groupContextResolver.ResolveVisibleGroupIdsAsync().Returns(new List<Guid> { GroupId });
        GivenGroupRange(ClientId, Day.AddDays(2), null);

        var violations = await _guard.FindViolationsAsync([(ClientId, Day)], CancellationToken.None);

        violations.ShouldHaveSingleItem().Message.ShouldContain("membership");
    }

    [Test]
    public async Task OutsideTheMembership_TheRefusalCarriesTheMembershipCodeAndTheBounds_TextUnchanged()
    {
        GivenClients(NewClient(ClientId, NewMembership(Day.AddDays(-30), Day.AddDays(-1))));

        var ex = await Should.ThrowAsync<WorkBookingRefusedException>(() => EnsureAsync((ClientId, Day)));

        ex.Message.ShouldBe(
            "Anna Meier cannot be planned on 2026-10-14: the day is outside the membership period (2026-09-14 to 2026-10-13).");
        ex.ErrorCode.ShouldBe(WorkBookingRefusalCodes.OutsideMembershipPeriod);
        ex.Details[WorkBookingRefusalCodes.ClientIdField].ShouldBe(ClientId);
        ex.Details[WorkBookingRefusalCodes.DateField].ShouldBe(Day);
        ex.Details[WorkBookingRefusalCodes.FromField].ShouldBe(Day.AddDays(-30));
        ex.Details[WorkBookingRefusalCodes.UntilField].ShouldBe(Day.AddDays(-1));
    }

    [Test]
    public async Task OutsideTheGroupPeriod_TheRefusalCarriesTheGroupCode_AndAnOpenEndAsNull()
    {
        _groupContextResolver.ResolveVisibleGroupIdsAsync().Returns(new List<Guid> { GroupId });
        GivenGroupRange(ClientId, Day.AddDays(2), null);

        var ex = await Should.ThrowAsync<WorkBookingRefusedException>(() => EnsureAsync((ClientId, Day)));

        ex.Message.ShouldBe(
            "Anna Meier cannot be planned on 2026-10-14: the day is outside the group period of the employee (2026-10-16 to open).");
        ex.ErrorCode.ShouldBe(WorkBookingRefusalCodes.OutsideGroupPeriod);
        ex.Details[WorkBookingRefusalCodes.FromField].ShouldBe(Day.AddDays(2));
        ex.Details[WorkBookingRefusalCodes.UntilField].ShouldBeNull();
    }

    [Test]
    public async Task FindViolations_NameTheViolatedPeriodByCode()
    {
        GivenClients(
            NewClient(ClientId, NewMembership(Day.AddDays(1), null)),
            NewClient(OtherClientId, null));
        _groupContextResolver.ResolveVisibleGroupIdsAsync().Returns(new List<Guid> { GroupId });
        GivenGroupRange(OtherClientId, Day.AddDays(2), null);

        var violations = await _guard.FindViolationsAsync([(ClientId, Day), (OtherClientId, Day)], CancellationToken.None);

        violations.Single(v => v.ClientId == ClientId).Code.ShouldBe(WorkBookingRefusalCodes.OutsideMembershipPeriod);
        violations.Single(v => v.ClientId == OtherClientId).Code.ShouldBe(WorkBookingRefusalCodes.OutsideGroupPeriod);
    }

    [Test]
    public async Task FindViolations_InsideEveryPeriod_IsEmpty()
    {
        GivenClients(NewClient(ClientId, NewMembership(Day, Day)));

        var violations = await _guard.FindViolationsAsync([(ClientId, Day)], CancellationToken.None);

        violations.ShouldBeEmpty();
    }

    [Test]
    public async Task FindViolationsInGroups_ReadsTheGivenGroups_NotTheRequestsGroupContext()
    {
        var targetGroup = Guid.NewGuid();
        _groupContextResolver.ResolveVisibleGroupIdsAsync().Returns(new List<Guid> { GroupId });
        GivenGroupRange(ClientId, Day.AddDays(1), null);

        var violations = await _guard.FindViolationsInGroupsAsync([(ClientId, Day)], [targetGroup], CancellationToken.None);

        violations.ShouldHaveSingleItem().Message.ShouldContain("group period");
        await _groupItemRepository.Received(1).GetValidityRangesAsync(
            Arg.Any<IReadOnlyCollection<Guid>>(),
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Single() == targetGroup),
            Arg.Any<CancellationToken>());
        await _groupContextResolver.DidNotReceive().ResolveVisibleGroupIdsAsync();
    }

    [Test]
    public async Task NoCells_ReadNothing()
    {
        await _guard.EnsureWithinPeriodsAsync([], CancellationToken.None);

        await _clientRepository.DidNotReceiveWithAnyArgs().GetByIdsAsync(
            Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>());
    }
}
