// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Every new cut piece starts with default expenses of its own (fresh rows): the rows the request carries, else copies of
/// the rows of the shift it is cut from. A sealed order as parent passes on the rows of its plannable shift converted in
/// the same batch (that copy is authoritative, so all parts carry what the first part keeps), and only its own rows when
/// the batch converts none; a piece created earlier in the batch passes on its rows.
/// </summary>

using Klacks.Api.Application.Commands.Shifts;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Application.Services.Schedules;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Services.Schedules;

[TestFixture]
public class ShiftCutFacadeDefaultExpensesTests
{
    private static readonly DateOnly FromDate = new(2026, 1, 1);

    private IShiftRepository _shiftRepository = null!;
    private IShiftExpensesRepository _expensesRepository = null!;
    private ShiftCutFacade _facade = null!;

    private readonly Guid _sealedOrderId = Guid.NewGuid();
    private readonly Guid _plannableId = Guid.NewGuid();
    private readonly Dictionary<Guid, Shift> _stored = new();
    private readonly Dictionary<Guid, Shift> _tracked = new();
    private readonly Dictionary<Guid, List<ShiftExpenses>> _storedExpenses = new();

    [SetUp]
    public void SetUp()
    {
        _stored.Clear();
        _tracked.Clear();
        _storedExpenses.Clear();
        _shiftRepository = Substitute.For<IShiftRepository>();
        _expensesRepository = Substitute.For<IShiftExpensesRepository>();
        _expensesRepository.GetByShiftIdsAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<IEnumerable<Guid>>()
                .SelectMany(id => _storedExpenses.GetValueOrDefault(id) ?? [])
                .ToList());
        var requirements = Substitute.For<IShiftRequiredQualificationRepository>();
        requirements.GetEffectiveByShiftIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<EffectiveShiftRequirement>());
        _shiftRepository.PutCutUpdate(Arg.Any<Shift>()).Returns(call =>
        {
            var shift = call.Arg<Shift>();
            _tracked[shift.Id] = shift;
            return shift;
        });
        _shiftRepository.Get(Arg.Any<Guid>()).Returns(call => _stored.GetValueOrDefault(call.Arg<Guid>()));
        _shiftRepository.GetTrackedOrFromDb(Arg.Any<Guid>())
            .Returns(call => _tracked.GetValueOrDefault(call.Arg<Guid>()) ?? _stored.GetValueOrDefault(call.Arg<Guid>()));
        _shiftRepository.When(r => r.Add(Arg.Any<Shift>())).Do(call => _tracked[call.Arg<Shift>().Id] = call.Arg<Shift>());

        _facade = new ShiftCutFacade(
            _shiftRepository,
            requirements,
            Substitute.For<IShiftTreeService>(),
            Substitute.For<IShiftResetService>(),
            Substitute.For<IShiftValidator>(),
            new ScheduleMapper(),
            Substitute.For<IUnitOfWork>(),
            ShiftVisibilityGuards.AllowAll(),
            _expensesRepository,
            Substitute.For<ILogger<ShiftCutFacade>>());

        Store(_sealedOrderId, ShiftStatus.SealedOrder, null);
        Store(_plannableId, ShiftStatus.OriginalShift, _sealedOrderId);
        _storedExpenses[_sealedOrderId] = [Expense(_sealedOrderId, "Meal", 12.5m)];
        _storedExpenses[_plannableId] = [Expense(_plannableId, "Meal", 12.5m), Expense(_plannableId, "Parking", 5m)];
    }

    [Test]
    public async Task Pieces_OfTheSealedOrder_CopyTheRowsOfThePlannableShiftConvertedInTheBatch()
    {
        var second = Guid.NewGuid();
        var third = Guid.NewGuid();

        await _facade.ProcessBatchCutsAsync(
        [
            Update(Piece(_plannableId)),
            Create(_sealedOrderId, Piece(second)),
            Create(_sealedOrderId, Piece(third))
        ]);

        foreach (var pieceId in new[] { second, third })
        {
            var rows = _tracked[pieceId].ShiftExpenses;
            rows.Select(e => e.Description).ShouldBe(["Meal", "Parking"]);
            rows.ShouldAllBe(e => e.ShiftId == pieceId);
            rows.ShouldAllBe(e => _storedExpenses[_plannableId].All(source => source.Id != e.Id));
        }

        _tracked[second].ShiftExpenses.Select(e => e.Id).Intersect(_tracked[third].ShiftExpenses.Select(e => e.Id)).ShouldBeEmpty();
    }

    [Test]
    public async Task Piece_OfTheSealedOrder_WithoutAConversionInTheBatch_CopiesTheOrdersRows()
    {
        var piece = Guid.NewGuid();

        await _facade.ProcessBatchCutsAsync([Create(_sealedOrderId, Piece(piece))]);

        _tracked[piece].ShiftExpenses.Select(e => e.Description).ShouldBe(["Meal"]);
    }

    [Test]
    public async Task Piece_OfAPieceCreatedEarlierInTheBatch_CopiesItsRows()
    {
        var child = Guid.NewGuid();
        var grandChild = Guid.NewGuid();

        await _facade.ProcessBatchCutsAsync(
        [
            Create(child, Piece(grandChild)),
            Create(_plannableId, Piece(child))
        ]);

        _tracked[grandChild].ShiftExpenses.Select(e => e.Description).ShouldBe(["Meal", "Parking"]);
        _tracked[grandChild].ShiftExpenses.ShouldAllBe(e => e.ShiftId == grandChild);
        await _expensesRepository.DidNotReceive().GetByShiftIdsAsync(
            Arg.Is<IEnumerable<Guid>>(ids => ids.Contains(child)), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Piece_WithRowsInTheRequest_KeepsOnlyThose_AsFreshRows()
    {
        var piece = Guid.NewGuid();
        var request = Piece(piece);
        var clonedRowId = _storedExpenses[_plannableId][0].Id;
        request.DefaultExpenses = [new ShiftExpensesResource { Id = clonedRowId, ShiftId = _plannableId, Amount = 7m, Description = "Cloned" }];

        await _facade.ProcessBatchCutsAsync([Create(_plannableId, request)]);

        var row = _tracked[piece].ShiftExpenses.ShouldHaveSingleItem();
        row.Description.ShouldBe("Cloned");
        row.Amount.ShouldBe(7m);
        row.ShiftId.ShouldBe(piece);
        row.Id.ShouldNotBe(clonedRowId);
    }

    private void Store(Guid id, ShiftStatus status, Guid? originalId) => _stored[id] = new Shift
    {
        Id = id,
        Name = "Stored",
        Status = status,
        OriginalId = originalId,
        FromDate = FromDate
    };

    private static ShiftExpenses Expense(Guid shiftId, string description, decimal amount) => new()
    {
        Id = Guid.NewGuid(),
        ShiftId = shiftId,
        Description = description,
        Amount = amount
    };

    private static ShiftResource Piece(Guid id) => new()
    {
        Id = id,
        Name = "Piece",
        Status = ShiftStatus.SplitShift,
        FromDate = FromDate
    };

    private static CutOperation Create(Guid parentId, ShiftResource data) =>
        new() { Type = CutOperation.CreateType, ParentId = parentId.ToString(), Data = data };

    private static CutOperation Update(ShiftResource data) =>
        new() { Type = CutOperation.UpdateType, ParentId = string.Empty, Data = data };
}
