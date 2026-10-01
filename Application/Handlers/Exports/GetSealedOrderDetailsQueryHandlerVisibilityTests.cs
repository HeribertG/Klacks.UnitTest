// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The sealed order detail preview lists every work entry of the order with employee name, hours and
/// surcharges. Work entries of employees hidden from the caller by group visibility must be left out, and a
/// missing order must still be answered with null.
/// </summary>

using Klacks.Api.Application.DTOs.Exports;
using Klacks.Api.Application.Handlers.Exports;
using Klacks.Api.Application.Interfaces.Exports;
using Klacks.Api.Application.Queries.Exports;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.Exports;

[TestFixture]
public class GetSealedOrderDetailsQueryHandlerVisibilityTests
{
    private ISealedOrderDetailsLoader _loader = null!;

    [SetUp]
    public void SetUp()
    {
        _loader = Substitute.For<ISealedOrderDetailsLoader>();
    }

    private GetSealedOrderDetailsQueryHandler CreateHandler(IClientVisibilityGuard clientVisibilityGuard)
    {
        return new GetSealedOrderDetailsQueryHandler(
            _loader,
            clientVisibilityGuard,
            Substitute.For<ILogger<GetSealedOrderDetailsQueryHandler>>());
    }

    [Test]
    public async Task WorkEntriesOfHiddenEmployees_AreLeftOut()
    {
        var orderId = Guid.NewGuid();
        var visibleEmployeeId = Guid.NewGuid();
        var hiddenEmployeeId = Guid.NewGuid();
        _loader.LoadAsync(orderId, Arg.Any<DateOnly?>(), Arg.Any<DateOnly?>(), Arg.Any<CancellationToken>())
            .Returns(new SealedOrderDetailsResource
            {
                Id = orderId,
                WorkEntries =
                [
                    new SealedOrderWorkEntryResource { WorkId = Guid.NewGuid(), EmployeeId = visibleEmployeeId },
                    new SealedOrderWorkEntryResource { WorkId = Guid.NewGuid(), EmployeeId = hiddenEmployeeId }
                ]
            });

        var result = await CreateHandler(TestGroupWriteVisibility.ClientsHidden(hiddenEmployeeId))
            .Handle(new GetSealedOrderDetailsQuery(orderId, null, null), CancellationToken.None);

        result.ShouldNotBeNull();
        result.WorkEntries.Select(w => w.EmployeeId).ShouldBe([visibleEmployeeId]);
    }

    [Test]
    public async Task MissingOrder_IsAnsweredWithNull()
    {
        _loader.LoadAsync(Arg.Any<Guid>(), Arg.Any<DateOnly?>(), Arg.Any<DateOnly?>(), Arg.Any<CancellationToken>())
            .Returns((SealedOrderDetailsResource?)null);

        var result = await CreateHandler(TestGroupWriteVisibility.AllClientsVisible())
            .Handle(new GetSealedOrderDetailsQuery(Guid.NewGuid(), null, null), CancellationToken.None);

        result.ShouldBeNull();
    }
}
