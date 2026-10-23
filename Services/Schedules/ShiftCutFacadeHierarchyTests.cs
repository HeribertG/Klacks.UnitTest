// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The cut batch owns the shift hierarchy: an UPDATE keeps the stored order reference, customer and tree fields, a
/// CREATE takes order reference and customer from its parent (stored or earlier in the batch), the cut dates are
/// validated against that derived order, and an UPDATE of a sealed order, a draft or with any status other than
/// SplitShift is refused with a BadRequestException before anything is staged.
/// </summary>

using Klacks.Api.Application.Commands.Shifts;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Application.Services.Schedules;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Services.Schedules;

[TestFixture]
public class ShiftCutFacadeHierarchyTests
{
    private static readonly DateOnly FromDate = new(2026, 1, 1);

    private IShiftRepository _shiftRepository = null!;
    private IShiftTreeService _shiftTreeService = null!;
    private IShiftValidator _shiftValidator = null!;
    private IUnitOfWork _unitOfWork = null!;
    private ShiftCutFacade _facade = null!;

    private readonly Guid _sealedOrderId = Guid.NewGuid();
    private readonly Guid _originalShiftId = Guid.NewGuid();
    private readonly Guid _pieceId = Guid.NewGuid();
    private readonly Guid _customerId = Guid.NewGuid();
    private readonly Guid _foreignId = Guid.NewGuid();
    private readonly Dictionary<Guid, Shift> _stored = new();
    private readonly Dictionary<Guid, Shift> _tracked = new();

    [SetUp]
    public void SetUp()
    {
        _stored.Clear();
        _tracked.Clear();
        _shiftRepository = Substitute.For<IShiftRepository>();
        _shiftTreeService = Substitute.For<IShiftTreeService>();
        _shiftValidator = Substitute.For<IShiftValidator>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        var requirements = Substitute.For<IShiftRequiredQualificationRepository>();
        requirements.GetEffectiveByShiftIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<EffectiveShiftRequirement>());
        _shiftRepository.PutCutUpdate(Arg.Any<Shift>()).Returns(call => call.Arg<Shift>());
        _shiftRepository.Get(Arg.Any<Guid>()).Returns(call => _stored.GetValueOrDefault(call.Arg<Guid>()));
        _shiftRepository.GetTrackedOrFromDb(Arg.Any<Guid>())
            .Returns(call => _tracked.GetValueOrDefault(call.Arg<Guid>()) ?? _stored.GetValueOrDefault(call.Arg<Guid>()));
        _shiftRepository.When(r => r.Add(Arg.Any<Shift>())).Do(call => _tracked[call.Arg<Shift>().Id] = call.Arg<Shift>());

        _facade = new ShiftCutFacade(
            _shiftRepository,
            requirements,
            _shiftTreeService,
            Substitute.For<IShiftResetService>(),
            _shiftValidator,
            new ScheduleMapper(),
            _unitOfWork,
            ShiftVisibilityGuards.AllowAll(),
            NoStoredExpenses(),
            Substitute.For<ILogger<ShiftCutFacade>>());
    }

    [Test]
    public async Task Create_FromStoredSealedOrder_TakesOrderIdAndCustomerOfTheOrder_NotOfTheRequest()
    {
        Store(Stored(_sealedOrderId, ShiftStatus.SealedOrder, originalId: null));
        var piece = Piece(_pieceId, originalId: _foreignId, clientId: _foreignId);

        await _facade.ProcessBatchCutsAsync([Create(_sealedOrderId, piece)]);

        var added = AddedShift(_pieceId);
        added.OriginalId.ShouldBe(_sealedOrderId);
        added.ClientId.ShouldBe(_customerId);
        await _shiftValidator.Received(1).ValidateCutDatesWithinAllowedRange(
            _sealedOrderId, piece.FromDate, piece.UntilDate, _shiftRepository);
        await _shiftValidator.DidNotReceive().ValidateCutDatesWithinAllowedRange(
            _foreignId, Arg.Any<DateOnly>(), Arg.Any<DateOnly?>(), Arg.Any<IShiftRepository>());
    }

    [Test]
    public async Task Create_FromStoredPlannableShift_TakesTheOrderReferenceOfTheParent()
    {
        Store(Stored(_originalShiftId, ShiftStatus.OriginalShift, originalId: _sealedOrderId));

        await _facade.ProcessBatchCutsAsync([Create(_originalShiftId, Piece(_pieceId, originalId: null, clientId: null))]);

        var added = AddedShift(_pieceId);
        added.OriginalId.ShouldBe(_sealedOrderId);
        added.ClientId.ShouldBe(_customerId);
    }

    [Test]
    public async Task Create_ChainInTheSameBatch_PassesTheResolvedOrderReferenceDown()
    {
        Store(Stored(_originalShiftId, ShiftStatus.OriginalShift, originalId: _sealedOrderId));
        var childId = Guid.NewGuid();
        var grandChildId = Guid.NewGuid();
        var grandChild = Piece(grandChildId, originalId: _foreignId, clientId: _foreignId);
        var child = Piece(childId, originalId: _foreignId, clientId: _foreignId);

        await _facade.ProcessBatchCutsAsync(
        [
            Create(childId, grandChild),
            Update(Piece(_originalShiftId, originalId: _foreignId, clientId: _foreignId)),
            Create(_originalShiftId, child)
        ]);

        foreach (var id in new[] { childId, grandChildId })
        {
            var added = AddedShift(id);
            added.OriginalId.ShouldBe(_sealedOrderId);
            added.ClientId.ShouldBe(_customerId);
        }

        await _shiftValidator.Received(3).ValidateCutDatesWithinAllowedRange(
            _sealedOrderId, Arg.Any<DateOnly>(), Arg.Any<DateOnly?>(), _shiftRepository);
    }

    [Test]
    public async Task Update_OfStoredPiece_KeepsStoredHierarchyAndCustomer_AndAppliesTheScalars()
    {
        var parentId = Guid.NewGuid();
        var rootId = Guid.NewGuid();
        var stored = Stored(_pieceId, ShiftStatus.SplitShift, originalId: _sealedOrderId);
        stored.ParentId = parentId;
        stored.RootId = rootId;
        stored.Lft = 3;
        stored.Rgt = 4;
        Store(stored);
        var update = Piece(_pieceId, originalId: _foreignId, clientId: _foreignId);
        update.ParentId = _foreignId;
        update.RootId = _foreignId;
        update.Lft = 1;
        update.Rgt = 99;
        update.StartShift = new TimeOnly(9, 0);

        await _facade.ProcessBatchCutsAsync([Update(update)]);

        var written = PutShift();
        written.OriginalId.ShouldBe(_sealedOrderId);
        written.ClientId.ShouldBe(_customerId);
        written.ParentId.ShouldBe(parentId);
        written.RootId.ShouldBe(rootId);
        written.Lft.ShouldBe(3);
        written.Rgt.ShouldBe(4);
        written.StartShift.ShouldBe(new TimeOnly(9, 0));
        _shiftTreeService.DidNotReceive().SetHierarchyRelation(Arg.Any<Shift>(), Arg.Any<Guid?>(), Arg.Any<Guid?>());
    }

    [Test]
    public async Task Update_OfStoredPlannableShift_BecomesARootPiece()
    {
        Store(Stored(_originalShiftId, ShiftStatus.OriginalShift, originalId: _sealedOrderId));

        await _facade.ProcessBatchCutsAsync([Update(Piece(_originalShiftId, originalId: _foreignId, clientId: _foreignId))]);

        var written = PutShift();
        written.OriginalId.ShouldBe(_sealedOrderId);
        written.ClientId.ShouldBe(_customerId);
        _shiftTreeService.Received(1).SetHierarchyRelation(written, null, null);
    }

    [TestCase(ShiftStatus.SealedOrder, ShiftStatus.SplitShift)]
    [TestCase(ShiftStatus.OriginalOrder, ShiftStatus.SplitShift)]
    [TestCase(ShiftStatus.OriginalShift, ShiftStatus.OriginalShift)]
    [TestCase(ShiftStatus.SplitShift, ShiftStatus.OriginalShift)]
    [TestCase(ShiftStatus.SplitShift, ShiftStatus.SealedOrder)]
    [TestCase(ShiftStatus.SplitShift, (ShiftStatus)99)]
    public async Task Update_WithForbiddenTransition_IsRefused_BeforeAnythingIsStaged(ShiftStatus storedStatus, ShiftStatus sentStatus)
    {
        Store(Stored(_originalShiftId, ShiftStatus.OriginalShift, originalId: _sealedOrderId));
        var targetId = Guid.NewGuid();
        Store(Stored(targetId, storedStatus, originalId: _sealedOrderId));
        var update = Piece(targetId, originalId: _sealedOrderId, clientId: _customerId);
        update.Status = sentStatus;

        await Should.ThrowAsync<BadRequestException>(() => _facade.ProcessBatchCutsAsync(
        [
            Create(_originalShiftId, Piece(_pieceId, originalId: _sealedOrderId, clientId: _customerId)),
            Update(update)
        ]));

        await _shiftRepository.DidNotReceiveWithAnyArgs().Add(default!);
        await _shiftRepository.DidNotReceiveWithAnyArgs().PutCutUpdate(default!);
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    private static IShiftExpensesRepository NoStoredExpenses()
    {
        var expenses = Substitute.For<IShiftExpensesRepository>();
        expenses.GetByShiftIdsAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>()).Returns(new List<ShiftExpenses>());
        return expenses;
    }

    private void Store(Shift shift) => _stored[shift.Id] = shift;

    private Shift Stored(Guid id, ShiftStatus status, Guid? originalId) => new()
    {
        Id = id,
        Name = "Stored",
        Status = status,
        OriginalId = originalId,
        ClientId = _customerId,
        FromDate = FromDate
    };

    private static ShiftResource Piece(Guid id, Guid? originalId, Guid? clientId) => new()
    {
        Id = id,
        Name = "Piece",
        Status = ShiftStatus.SplitShift,
        OriginalId = originalId,
        ClientId = clientId,
        FromDate = FromDate
    };

    private static CutOperation Create(Guid parentId, ShiftResource data) =>
        new() { Type = "CREATE", ParentId = parentId.ToString(), Data = data };

    private static CutOperation Update(ShiftResource data) =>
        new() { Type = "UPDATE", ParentId = string.Empty, Data = data };

    private Shift AddedShift(Guid id) => _tracked[id];

    private Shift PutShift() => _shiftRepository.ReceivedCalls()
        .Where(c => c.GetMethodInfo().Name == nameof(IShiftRepository.PutCutUpdate))
        .Select(c => (Shift)c.GetArguments()[0]!)
        .Single();
}
