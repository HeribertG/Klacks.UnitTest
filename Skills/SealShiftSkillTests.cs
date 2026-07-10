// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for SealShiftSkill: sealing an order is written inside a transaction and both the sealed
/// order and its newly created plannable shift are re-read from the database; a confirmed read reports
/// a verified success, a missing plannable shift rolls the write back and reports an error instead of a
/// false success, and orders that are not sealable (wrong status, missing group) are rejected up front.
/// </summary>

using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class SealShiftSkillTests
{
    private IShiftRepository _shiftRepository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private SealShiftSkill _skill = null!;

    private static readonly Guid OrderId = Guid.NewGuid();
    private static readonly Guid PlannableShiftId = Guid.NewGuid();

    [SetUp]
    public void Setup()
    {
        _shiftRepository = Substitute.For<IShiftRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _skill = new SealShiftSkill(_shiftRepository, _unitOfWork);

        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<Guid>>>())
            .Returns(ci => ci.Arg<Func<Task<Guid>>>()());
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanEditShifts" }
    };

    private static Dictionary<string, object> Params() => new()
    {
        ["shiftId"] = OrderId.ToString()
    };

    private static Shift SealableOrder() => new()
    {
        Id = OrderId,
        Status = ShiftStatus.OriginalOrder,
        Name = "Frühschicht Bern",
        Abbreviation = "FS",
        FromDate = new DateOnly(2026, 6, 1),
        IsMonday = true,
        Quantity = 1,
        SumEmployees = 2,
        GroupItems = new List<GroupItem> { new() { Id = Guid.NewGuid(), GroupId = Guid.NewGuid(), IsDeleted = false } }
    };

    [Test]
    public async Task Seals_AndReportsVerified_WhenPersistenceIsConfirmed()
    {
        var order = SealableOrder();
        _shiftRepository.Get(OrderId).Returns(order);
        _shiftRepository.PutWithSealedOrderHandling(Arg.Any<Shift>())
            .Returns(new Shift { Id = PlannableShiftId, OriginalId = OrderId, Status = ShiftStatus.OriginalShift, Name = order.Name });
        _shiftRepository.GetNoTracking(OrderId).Returns(new Shift { Id = OrderId, Status = ShiftStatus.SealedOrder });
        _shiftRepository.GetNoTracking(PlannableShiftId)
            .Returns(new Shift { Id = PlannableShiftId, OriginalId = OrderId, Status = ShiftStatus.OriginalShift });

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        Assert.That(result.Success, Is.True);
        Assert.That(result.Message, Does.Contain("verified"));
        await _shiftRepository.Received(1).PutWithSealedOrderHandling(
            Arg.Is<Shift>(s => s.Id == OrderId && s.Status == ShiftStatus.SealedOrder));
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task ReturnsError_AndRollsBack_WhenPlannableShiftCannotBeConfirmed()
    {
        var order = SealableOrder();
        _shiftRepository.Get(OrderId).Returns(order);
        _shiftRepository.PutWithSealedOrderHandling(Arg.Any<Shift>())
            .Returns(new Shift { Id = PlannableShiftId, OriginalId = OrderId, Status = ShiftStatus.OriginalShift, Name = order.Name });
        _shiftRepository.GetNoTracking(OrderId).Returns(new Shift { Id = OrderId, Status = ShiftStatus.SealedOrder });
        _shiftRepository.GetNoTracking(PlannableShiftId).Returns((Shift?)null);

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("rolled back"));
    }

    [Test]
    public async Task RejectsSealing_WhenShiftIsAlreadySealed()
    {
        var order = SealableOrder();
        order.Status = ShiftStatus.SealedOrder;
        _shiftRepository.Get(OrderId).Returns(order);

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("already sealed"));
        await _shiftRepository.DidNotReceive().PutWithSealedOrderHandling(Arg.Any<Shift>());
    }

    [Test]
    public async Task RejectsSealing_WhenNoGroupAssigned()
    {
        var order = SealableOrder();
        order.GroupItems = new List<GroupItem>();
        _shiftRepository.Get(OrderId).Returns(order);

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("at least one group"));
        await _shiftRepository.DidNotReceive().PutWithSealedOrderHandling(Arg.Any<Shift>());
    }
}
