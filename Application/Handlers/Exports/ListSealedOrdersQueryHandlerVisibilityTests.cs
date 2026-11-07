// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The sealed order list is reached by the Admin-only endpoint and by the list_sealed_orders skill. The handler both
/// paths share must hand the caller's shift group restriction to the loader (null for an unrestricted caller), so a
/// group-restricted caller never sees orders of hidden groups.
/// </summary>

using Klacks.Api.Application.DTOs.Exports;
using Klacks.Api.Application.Handlers.Exports;
using Klacks.Api.Application.Interfaces.Exports;
using Klacks.Api.Application.Queries.Exports;
using Klacks.Api.Domain.Services.ShiftSchedule;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.Exports;

[TestFixture]
public class ListSealedOrdersQueryHandlerVisibilityTests
{
    private ISealedOrderListLoader _loader = null!;
    private IShiftGroupFilterService _shiftGroupFilterService = null!;
    private ListSealedOrdersQueryHandler _handler = null!;

    [SetUp]
    public void SetUp()
    {
        _loader = Substitute.For<ISealedOrderListLoader>();
        _shiftGroupFilterService = Substitute.For<IShiftGroupFilterService>();
        _handler = new ListSealedOrdersQueryHandler(
            _loader, _shiftGroupFilterService, Substitute.For<ILogger<ListSealedOrdersQueryHandler>>());
    }

    [Test]
    public async Task RestrictedCaller_PassesTheVisibleGroupsToTheLoader()
    {
        var visibleGroup = Guid.NewGuid();
        _shiftGroupFilterService.ResolveShiftListGroupIdsAsync(null).Returns(new List<Guid> { visibleGroup });
        var expected = new List<SealedOrderListItem> { new() { Id = Guid.NewGuid() } };
        _loader.LoadAsync(null, null, null, null,
                Arg.Is<IReadOnlyCollection<Guid>?>(ids => ids != null && ids.Count == 1 && ids.Contains(visibleGroup)),
                CancellationToken.None)
            .Returns(expected);

        var result = await _handler.Handle(new ListSealedOrdersQuery(null, null, null, null), CancellationToken.None);

        result.ShouldBeSameAs(expected);
    }

    [Test]
    public async Task UnrestrictedCaller_PassesNoRestrictionToTheLoader()
    {
        _shiftGroupFilterService.ResolveShiftListGroupIdsAsync(null).Returns((List<Guid>?)null);

        await _handler.Handle(new ListSealedOrdersQuery(null, null, null, null), CancellationToken.None);

        await _loader.Received(1).LoadAsync(null, null, null, null, null, CancellationToken.None);
    }
}
