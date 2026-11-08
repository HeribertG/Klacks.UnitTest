// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Since the supervisor may delete a whole sealed order family (owner decision 2026-11-07), a group-restricted caller
/// who sees the addressed order must not take a plannable copy or cut piece of another group with it: the family is
/// refused as a whole when the visibility guard names a hidden row, and deleted when every row may be touched.
/// </summary>

using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Models.Schedules;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace Klacks.UnitTest.Application.Services.Schedules;

[TestFixture]
public class SealedOrderFamilyDeletionVisibilityTests
{
    private static readonly Guid OrderId = Guid.NewGuid();
    private static readonly Guid PlannableId = Guid.NewGuid();
    private static readonly Guid PieceId = Guid.NewGuid();

    private IShiftRepository _repository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private IShiftVisibilityGuard _visibility = null!;
    private SealedOrderFamilyDeletion _sut = null!;
    private Shift _order = null!;
    private List<Guid> _family = null!;

    [SetUp]
    public void SetUp()
    {
        _repository = Substitute.For<IShiftRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _visibility = Substitute.For<IShiftVisibilityGuard>();
        _sut = new SealedOrderFamilyDeletion(
            _repository, _unitOfWork, _visibility, Substitute.For<ILogger<SealedOrderFamilyDeletion>>());

        _order = new Shift { Id = OrderId, Name = "Order", Status = ShiftStatus.SealedOrder };
        _family = [OrderId, PlannableId, PieceId];
        _repository.GetOrderFamilyUsageAsync(OrderId, Arg.Any<CancellationToken>())
            .Returns(new OrderFamilyUsage(_family, HasWorks: false, HasScenarioClones: false, HasContainerReferences: false));
    }

    [Test]
    public async Task AFamilyWithARowOutsideTheCallersGroups_IsRefusedAndNothingIsDeleted()
    {
        _visibility.FindFirstHiddenAsync(
                Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.SequenceEqual(_family)), Arg.Any<CancellationToken>())
            .Returns(PieceId);

        await Should.ThrowAsync<ShiftLockedException>(() => _sut.DeleteAsync(_order, CancellationToken.None));

        await _repository.DidNotReceiveWithAnyArgs().DeleteOrderFamilyAsync(default!, default);
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task AFamilyWhoseRowsAreAllVisible_IsDeletedAsAWhole()
    {
        _visibility.FindFirstHiddenAsync(
                Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.SequenceEqual(_family)), Arg.Any<CancellationToken>())
            .Returns((Guid?)null);

        var deleted = await _sut.DeleteAsync(_order, CancellationToken.None);

        deleted.ShouldBe(_family.Count);
        await _repository.Received(1).DeleteOrderFamilyAsync(
            Arg.Is<IReadOnlyList<Guid>>(ids => ids.SequenceEqual(_family)), Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).CompleteAsync();
    }
}
