// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.Handlers.Shifts;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Application.Queries.Shifts;
using Klacks.Api.Domain.Models.Geo;
using Klacks.Api.Domain.Services.ShiftSchedule;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.Shifts;

/// <summary>
/// The shift list handler resolves location filters (state names in any language) to address codes and hands them to
/// the repository together with the caller's own group restriction for the customer visibility - also when a group is
/// selected, so a selected group never stands in for the caller's restriction. Without location filters the repository
/// receives no location filter at all.
/// </summary>
[TestFixture]
public class GetTruncatedListLocationFilterTests
{
    private static readonly Guid VisibleGroup = Guid.NewGuid();
    private static readonly Guid SelectedGroup = Guid.NewGuid();

    private IShiftRepository _shiftRepository = null!;
    private IShiftGroupFilterService _groupFilter = null!;
    private GetTruncatedListQueryHandler _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _shiftRepository = Substitute.For<IShiftRepository>();
        _shiftRepository
            .GetFilteredAndPaginatedShifts(Arg.Any<ShiftFilter>(), Arg.Any<IReadOnlyCollection<Guid>?>(), Arg.Any<ShiftLocationFilter?>())
            .Returns(new TruncatedShift { Shifts = [] });
        _groupFilter = Substitute.For<IShiftGroupFilterService>();
        _groupFilter.ResolveShiftListGroupIdsAsync(null).Returns(new List<Guid> { VisibleGroup });
        _groupFilter.ResolveShiftListGroupIdsAsync(SelectedGroup).Returns(new List<Guid> { SelectedGroup });
        _sut = new GetTruncatedListQueryHandler(
            _shiftRepository, _groupFilter, TestAddressLocationResolver.SwissAndGerman(), new ScheduleMapper(),
            Substitute.For<ILogger<GetTruncatedListQueryHandler>>());
    }

    [Test]
    public async Task StateName_IsResolved_AndTheCallersOwnRestrictionGuardsTheCustomers()
    {
        var filter = SealedOrders();
        filter.State = "Berne";
        filter.City = " Bern ";
        filter.SelectedGroup = SelectedGroup;

        await _sut.Handle(new GetTruncatedListQuery(filter), CancellationToken.None);

        await _shiftRepository.Received(1).GetFilteredAndPaginatedShifts(
            Arg.Is<ShiftFilter>(f => ReferenceEquals(f, filter)),
            Arg.Is<IReadOnlyCollection<Guid>?>(ids => ids != null && ids.Single() == SelectedGroup),
            Arg.Is<ShiftLocationFilter?>(l => l != null
                && l.Criteria.States.Single() == new StateCodeMatch("BE", "CH")
                && l.Criteria.City == "Bern"
                && l.CustomerVisibleGroupIds != null && l.CustomerVisibleGroupIds.Single() == VisibleGroup));
    }

    [Test]
    public async Task WithoutLocation_NoLocationFilterReachesTheRepository()
    {
        await _sut.Handle(new GetTruncatedListQuery(SealedOrders()), CancellationToken.None);

        await _shiftRepository.Received(1).GetFilteredAndPaginatedShifts(
            Arg.Any<ShiftFilter>(), Arg.Any<IReadOnlyCollection<Guid>?>(), Arg.Is<ShiftLocationFilter?>(l => l == null));
    }

    [Test]
    public async Task CountryAndStateLists_ReachTheRepositoryAsCriteria_WithTheCallersOwnRestriction()
    {
        var filter = SealedOrders();
        filter.Countries = ["ch", "DE"];
        filter.States = [new ShiftFilterState { State = "BE", Country = "CH" }, new ShiftFilterState { State = "BE", Country = "DE" }];
        filter.City = "Bern";

        await _sut.Handle(new GetTruncatedListQuery(filter), CancellationToken.None);

        await _shiftRepository.Received(1).GetFilteredAndPaginatedShifts(
            Arg.Any<ShiftFilter>(),
            Arg.Any<IReadOnlyCollection<Guid>?>(),
            Arg.Is<ShiftLocationFilter?>(l => l != null
                && l.Criteria.CountryCode == null
                && l.Criteria.CountryCodes != null && l.Criteria.CountryCodes.SequenceEqual(new[] { "CH", "DE" })
                && l.Criteria.States.SequenceEqual(new[] { new StateCodeMatch("BE", "CH"), new StateCodeMatch("BE", "DE") })
                && l.Criteria.City == "Bern"
                && l.CustomerVisibleGroupIds != null && l.CustomerVisibleGroupIds.Single() == VisibleGroup));
    }

    [Test]
    public async Task EmptyLists_AreNoLocationFilter()
    {
        var filter = SealedOrders();
        filter.Countries = [];
        filter.States = [];

        await _sut.Handle(new GetTruncatedListQuery(filter), CancellationToken.None);

        await _shiftRepository.Received(1).GetFilteredAndPaginatedShifts(
            Arg.Any<ShiftFilter>(), Arg.Any<IReadOnlyCollection<Guid>?>(), Arg.Is<ShiftLocationFilter?>(l => l == null));
    }

    private static ShiftFilter SealedOrders() => new()
    {
        FilterType = ShiftFilterType.Original,
        IsSealedOrder = true,
        ActiveDateRange = true,
        NumberOfItemsPerPage = 20
    };
}
