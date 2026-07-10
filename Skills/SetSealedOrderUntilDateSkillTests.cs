// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for SetSealedOrderUntilDateSkill: the until-date is written inside a transaction and
/// re-read from the database; a confirmed read reports a verified success, and the one-time-only
/// guard (not yet sealed, until-date already set, or work already planned past the requested date)
/// rejects the change up front instead of writing.
/// </summary>

using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Schedules;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class SetSealedOrderUntilDateSkillTests
{
    private IShiftRepository _shiftRepository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private SetSealedOrderUntilDateSkill _skill = null!;

    private static readonly Guid OrderId = Guid.NewGuid();
    private static readonly DateOnly FromDate = new(2026, 6, 1);
    private static readonly DateOnly UntilDate = new(2026, 12, 31);

    [SetUp]
    public void Setup()
    {
        _shiftRepository = Substitute.For<IShiftRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _skill = new SetSealedOrderUntilDateSkill(_shiftRepository, _unitOfWork);

        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<Guid>>>())
            .Returns(ci => ci.Arg<Func<Task<Guid>>>()());

        _shiftRepository.HasWorksAfterDateForOrderTreeAsync(OrderId, UntilDate, Arg.Any<CancellationToken>())
            .Returns(false);
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
        ["shiftId"] = OrderId.ToString(),
        ["untilDate"] = UntilDate.ToString("yyyy-MM-dd")
    };

    private static Shift OpenEndedSealedOrder() => new()
    {
        Id = OrderId,
        Status = ShiftStatus.SealedOrder,
        Name = "Reinigung Müller",
        FromDate = FromDate,
        UntilDate = null
    };

    [Test]
    public async Task SetsUntilDate_AndReportsVerified_WhenNoFutureWorks()
    {
        var order = OpenEndedSealedOrder();
        _shiftRepository.Get(OrderId).Returns(order);
        _shiftRepository.PutWithSealedOrderHandling(Arg.Any<Shift>())
            .Returns(ci => ci.Arg<Shift>());
        _shiftRepository.GetNoTracking(OrderId)
            .Returns(new Shift { Id = OrderId, Status = ShiftStatus.SealedOrder, UntilDate = UntilDate });

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        Assert.That(result.Success, Is.True);
        Assert.That(result.Message, Does.Contain("verified"));
        await _shiftRepository.Received(1).PutWithSealedOrderHandling(
            Arg.Is<Shift>(s => s.Id == OrderId && s.UntilDate == UntilDate));
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task RejectsSetting_WhenUntilDateAlreadySet()
    {
        var order = OpenEndedSealedOrder();
        order.UntilDate = new DateOnly(2026, 9, 1);
        _shiftRepository.Get(OrderId).Returns(order);

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("already has an until-date"));
        await _shiftRepository.DidNotReceive().PutWithSealedOrderHandling(Arg.Any<Shift>());
    }

    [Test]
    public async Task RejectsSetting_WhenFutureWorksExist()
    {
        var order = OpenEndedSealedOrder();
        _shiftRepository.Get(OrderId).Returns(order);
        _shiftRepository.HasWorksAfterDateForOrderTreeAsync(OrderId, UntilDate, Arg.Any<CancellationToken>())
            .Returns(true);

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("already planned after that date"));
        await _shiftRepository.DidNotReceive().PutWithSealedOrderHandling(Arg.Any<Shift>());
    }

    [Test]
    public async Task RejectsSetting_WhenNotSealed()
    {
        var order = OpenEndedSealedOrder();
        order.Status = ShiftStatus.OriginalOrder;
        _shiftRepository.Get(OrderId).Returns(order);

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("not sealed yet"));
        await _shiftRepository.DidNotReceive().PutWithSealedOrderHandling(Arg.Any<Shift>());
    }
}
