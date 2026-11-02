// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for ShiftFamilyEndService: every refusal comes before any write, the lock comes before the first read, the
/// addressed row is saved inside the transaction, the family is trimmed, and a removed part is deleted before its tree is
/// numbered in a second save.
/// </summary>

using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Services.Shifts;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Services.Shifts;

[TestFixture]
public class ShiftFamilyEndServiceTests
{
    private static readonly Guid OrderId = Guid.NewGuid();
    private static readonly DateOnly Start = new(2026, 1, 1);
    private static readonly DateOnly Day = new(2026, 3, 31);

    private IShiftRepository _shiftRepository = null!;
    private IOrderFamilyEndRepository _familyRepository = null!;
    private IShiftTreeService _treeService = null!;
    private IUnitOfWork _unitOfWork = null!;
    private ShiftFamilyEndService _service = null!;

    [SetUp]
    public void Setup()
    {
        _shiftRepository = Substitute.For<IShiftRepository>();
        _familyRepository = Substitute.For<IOrderFamilyEndRepository>();
        _treeService = Substitute.For<IShiftTreeService>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<Guid>>>())
            .Returns(ci => ci.Arg<Func<Task<Guid>>>()());
        _familyRepository.GetUsageAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(ci => new OrderFamilyUsage(ci.Arg<IReadOnlyList<Guid>>(), false, false, false));

        _service = ServiceSeeing(ShiftVisibilityGuards.AllowAll());
    }

    private ShiftFamilyEndService ServiceSeeing(IShiftVisibilityGuard guard) => new(
        _shiftRepository, _familyRepository, _treeService, guard, _unitOfWork, Substitute.For<ILogger<ShiftFamilyEndService>>());

    private static Shift Order(DateOnly? until = null) => new()
    {
        Id = OrderId,
        Name = "Night patrol",
        Status = ShiftStatus.SealedOrder,
        FromDate = Start,
        UntilDate = until
    };

    private static Shift Part(ShiftStatus status, DateOnly from, DateOnly? until = null, Guid? rootId = null) => new()
    {
        Id = Guid.NewGuid(),
        OriginalId = OrderId,
        Name = "Part",
        Status = status,
        FromDate = from,
        UntilDate = until,
        RootId = rootId
    };

    private static Shift Standalone(DateOnly? until = null) => new()
    {
        Id = OrderId,
        Name = "Night patrol",
        Status = ShiftStatus.OriginalShift,
        ShiftType = ShiftType.IsContainer,
        FromDate = Start,
        UntilDate = until
    };

    private void Family(params Shift[] rows) =>
        _familyRepository.GetTrackedFamilyAsync(OrderId, Arg.Any<CancellationToken>()).Returns(rows.ToList());

    private async Task<ShiftEndDateRefusedException> RefusedAsync(Shift entry, DateOnly day, string reason)
    {
        var thrown = await Should.ThrowAsync<ShiftEndDateRefusedException>(
            () => _service.EndFamilyAsync(entry, day, null, CancellationToken.None));

        thrown.ConflictCode.ShouldBe(ShiftEndDateRefusedException.ErrorCode);
        thrown.ConflictDetails![ShiftEndDateRefusedException.ReasonKey].ShouldBe(reason);
        await _unitOfWork.DidNotReceive().CompleteAsync();
        await _shiftRepository.DidNotReceiveWithAnyArgs().DeleteOrderFamilyAsync(default!, default);
        return thrown;
    }

    [Test]
    public async Task EndsTheFamily_FromTheSealedOrder_FromThePlanRow_AndFromACut()
    {
        foreach (var pick in new Func<Shift, Shift, Shift, Shift>[] { (o, _, _) => o, (_, p, _) => p, (_, _, c) => c })
        {
            var order = Order();
            var plan = Part(ShiftStatus.OriginalShift, Start);
            var cut = Part(ShiftStatus.SplitShift, Start);
            var endsLater = Part(ShiftStatus.SplitShift, Start, new DateOnly(2026, 6, 30));
            Family(order, plan, cut, endsLater);

            await _service.EndFamilyAsync(pick(order, plan, cut), Day, null, CancellationToken.None);

            order.UntilDate.ShouldBe(Day);
            plan.UntilDate.ShouldBe(Day);
            cut.UntilDate.ShouldBe(Day);
            endsLater.UntilDate.ShouldBe(Day);
        }

        await _familyRepository.Received(3).LockHeadAsync(OrderId, Arg.Any<CancellationToken>());
        await _unitOfWork.Received(3).CompleteAsync();
    }

    [Test]
    public async Task LocksTheOrderBeforeItReadsTheFamily()
    {
        var order = Order();
        Family(order);

        await _service.EndFamilyAsync(order, Day, null, CancellationToken.None);

        Received.InOrder(() =>
        {
            _unitOfWork.DiscardTrackedChanges();
            _familyRepository.LockHeadAsync(OrderId, Arg.Any<CancellationToken>());
            _familyRepository.GetTrackedFamilyAsync(OrderId, Arg.Any<CancellationToken>());
        });
    }

    [Test]
    public async Task ARetriedTransaction_ChecksAgainstTheDatabase_NotAgainstTheFailedRun()
    {
        var order = Order();
        var plan = Part(ShiftStatus.OriginalShift, Start);
        _familyRepository.GetTrackedFamilyAsync(OrderId, Arg.Any<CancellationToken>())
            .Returns([order, plan], [Order(Day.AddDays(5)), plan]);
        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<Guid>>>()).Returns(async ci =>
        {
            var operation = ci.Arg<Func<Task<Guid>>>();
            await operation();
            return await operation();
        });

        var thrown = await Should.ThrowAsync<ShiftEndDateRefusedException>(
            () => _service.EndFamilyAsync(plan, Day, null, CancellationToken.None));

        thrown.ConflictDetails![ShiftEndDateRefusedException.ReasonKey].ShouldBe(ShiftEndDateRefusedException.StoredEndReason);
        _unitOfWork.Received(2).DiscardTrackedChanges();
    }

    [Test]
    public async Task SavesTheAddressedRowInsideTheTransaction_BeforeTheCommit()
    {
        var order = Order();
        Family(order);
        var saved = false;
        var savedAtCommit = false;
        _unitOfWork.When(unit => unit.CompleteAsync()).Do(_ => savedAtCommit = saved);

        await _service.EndFamilyAsync(order, Day, () => { saved = true; return Task.CompletedTask; }, CancellationToken.None);

        savedAtCommit.ShouldBeTrue("the addressed row is saved before the commit");
        await _unitOfWork.Received(1).ExecuteInTransactionAsync(Arg.Any<Func<Task<Guid>>>());
    }

    [Test]
    public async Task RemovesAPartStartingAfterTheDay_ThenNumbersItsTree_InASecondSave()
    {
        var rootId = Guid.NewGuid();
        var order = Order();
        var plan = Part(ShiftStatus.OriginalShift, Start);
        var later = Part(ShiftStatus.SplitShift, Day.AddDays(1), rootId: rootId);
        Family(order, plan, later);

        await _service.EndFamilyAsync(order, Day, null, CancellationToken.None);

        later.UntilDate.ShouldBeNull("a removed part is not trimmed");
        Received.InOrder(() =>
        {
            _shiftRepository.DeleteOrderFamilyAsync(
                Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Single() == later.Id), Arg.Any<CancellationToken>());
            _unitOfWork.CompleteAsync();
            _treeService.RecalculateTreesAsync(
                Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Single() == rootId), Arg.Any<CancellationToken>());
            _unitOfWork.CompleteAsync();
        });
    }

    [Test]
    public async Task NoPartToRemove_NumbersNoTree()
    {
        var order = Order();
        Family(order);

        await _service.EndFamilyAsync(order, Day, null, CancellationToken.None);

        await _treeService.DidNotReceiveWithAnyArgs().RecalculateTreesAsync(default!, default);
        await _familyRepository.DidNotReceiveWithAnyArgs().GetUsageAsync(default!, default);
    }

    [Test]
    public async Task ADayBeforeTheFamilyStart_IsRefused()
    {
        var order = Order();
        Family(order);

        await RefusedAsync(order, Start.AddDays(-1), ShiftEndDateRefusedException.BeforeStartReason);
    }

    [Test]
    public async Task ADayBeforeTheStartOfTheAddressedPart_IsRefused()
    {
        var later = Part(ShiftStatus.SplitShift, Day.AddDays(1));
        Family(Order(), later);

        await RefusedAsync(later, Day, ShiftEndDateRefusedException.BeforeStartReason);
    }

    [Test]
    public async Task TheFirstDayOfTheFamily_IsAccepted()
    {
        var order = Order();
        Family(order);

        await _service.EndFamilyAsync(order, Start, null, CancellationToken.None);

        order.UntilDate.ShouldBe(Start);
    }

    [TestCase(1)]
    [TestCase(-1)]
    public async Task ASealedOrderThatEndsOnAnotherDay_IsRefused(int offset)
    {
        var plan = Part(ShiftStatus.OriginalShift, Start);
        Family(Order(Day.AddDays(offset)), plan);

        await RefusedAsync(plan, Day, ShiftEndDateRefusedException.StoredEndReason);
    }

    [Test]
    public async Task ASealedOrderThatEndsOnTheSameDay_BringsTheRowsInLine()
    {
        var plan = Part(ShiftStatus.OriginalShift, Start);
        Family(Order(Day), plan);

        await _service.EndFamilyAsync(plan, Day, null, CancellationToken.None);

        plan.UntilDate.ShouldBe(Day);
    }

    [Test]
    public async Task AnAddressedRowThatGotAnEndMeanwhile_IsRefused()
    {
        var stale = Part(ShiftStatus.OriginalShift, Start);
        var current = Part(ShiftStatus.OriginalShift, Start, Day.AddDays(5));
        current.Id = stale.Id;
        Family(Order(), current);

        await RefusedAsync(stale, Day, ShiftEndDateRefusedException.StoredEndReason);
    }

    [Test]
    public async Task AWorkAfterTheDay_IsRefused_AndNamesTheLastWork()
    {
        var order = Order();
        Family(order, Part(ShiftStatus.OriginalShift, Start));
        _familyRepository.FindLastWorkDateAfterAsync(OrderId, Day, Arg.Any<CancellationToken>()).Returns(new DateOnly(2026, 4, 2));

        var thrown = await RefusedAsync(order, Day, ShiftEndDateRefusedException.WorksAfterReason);

        thrown.ConflictDetails![ShiftEndDateRefusedException.LastWorkDateKey].ShouldBe("2026-04-02");
        thrown.Message.ShouldContain("2026-04-02");
    }

    [Test]
    public async Task AScenarioWorkAfterTheDay_IsRefusedWithItsOwnReason_AndNamesTheDate()
    {
        var order = Order();
        Family(order, Part(ShiftStatus.OriginalShift, Start));
        _familyRepository.FindLastScenarioWorkDateAfterAsync(OrderId, Day, Arg.Any<CancellationToken>()).Returns(new DateOnly(2026, 4, 3));

        var thrown = await RefusedAsync(order, Day, ShiftEndDateRefusedException.ScenarioWorksAfterReason);

        thrown.ConflictDetails!.ContainsKey(ShiftEndDateRefusedException.LastWorkDateKey).ShouldBeFalse("the date of a scenario does not go out");
        thrown.Message.ShouldContain("a scenario holds work");
        thrown.Message.ShouldNotContain("2026-04-03");
        order.UntilDate.ShouldBeNull();
    }

    [Test]
    public async Task AScenarioWorkIsLookedForAfterTheRequestedDay_OnTheOrderOfTheAddressedRow()
    {
        var order = Order();
        var plan = Part(ShiftStatus.OriginalShift, Start);
        Family(order, plan);

        await _service.EndFamilyAsync(plan, Day, null, CancellationToken.None);

        await _familyRepository.Received(1).FindLastScenarioWorkDateAfterAsync(OrderId, Day, Arg.Any<CancellationToken>());
        order.UntilDate.ShouldBe(Day);
    }

    [Test]
    public async Task ARealWorkAfterTheDay_KeepsItsOwnReason_WhenAScenarioHasOneToo()
    {
        var order = Order();
        Family(order);
        _familyRepository.FindLastWorkDateAfterAsync(OrderId, Day, Arg.Any<CancellationToken>()).Returns(Day.AddDays(1));
        _familyRepository.FindLastScenarioWorkDateAfterAsync(OrderId, Day, Arg.Any<CancellationToken>()).Returns(Day.AddDays(2));

        await RefusedAsync(order, Day, ShiftEndDateRefusedException.WorksAfterReason);
    }

    [Test]
    public async Task AHiddenRow_RefusesTheWholeFamily_BeforeAnyDateIsRead()
    {
        var order = Order(Day.AddDays(5));
        var plan = Part(ShiftStatus.OriginalShift, Start);
        var hiddenCut = Part(ShiftStatus.SplitShift, Start);
        Family(order, plan, hiddenCut);
        _familyRepository.FindLastWorkDateAfterAsync(OrderId, Day, Arg.Any<CancellationToken>()).Returns(new DateOnly(2026, 4, 2));

        var thrown = await Should.ThrowAsync<ShiftEndDateRefusedException>(
            () => ServiceSeeing(ShiftVisibilityGuards.Hiding(hiddenCut.Id)).EndFamilyAsync(plan, Day, null, CancellationToken.None));

        thrown.ConflictDetails!.Keys.ShouldBe([ShiftEndDateRefusedException.ReasonKey]);
        thrown.ConflictDetails![ShiftEndDateRefusedException.ReasonKey].ShouldBe(ShiftEndDateRefusedException.HiddenRowsReason);
        thrown.Message.ShouldNotContain("2026");
        await _familyRepository.DidNotReceiveWithAnyArgs().FindLastWorkDateAfterAsync(default, default, default);
        await _unitOfWork.DidNotReceive().CompleteAsync();
        hiddenCut.UntilDate.ShouldBeNull();
        plan.UntilDate.ShouldBeNull();
    }

    [Test]
    public async Task AHiddenSealedOrder_RefusesToo()
    {
        var plan = Part(ShiftStatus.OriginalShift, Start);
        Family(Order(), plan);

        var thrown = await Should.ThrowAsync<ShiftEndDateRefusedException>(
            () => ServiceSeeing(ShiftVisibilityGuards.Hiding(OrderId)).EndFamilyAsync(plan, Day, null, CancellationToken.None));

        thrown.ConflictDetails![ShiftEndDateRefusedException.ReasonKey].ShouldBe(ShiftEndDateRefusedException.HiddenRowsReason);
    }

    [Test]
    public async Task ARefusalMessage_NamesNoShift()
    {
        var order = Order();
        Family(order, Part(ShiftStatus.OriginalShift, Start));
        _familyRepository.FindLastWorkDateAfterAsync(OrderId, Day, Arg.Any<CancellationToken>()).Returns(new DateOnly(2026, 4, 2));

        var thrown = await RefusedAsync(order, Day, ShiftEndDateRefusedException.WorksAfterReason);

        thrown.Message.ShouldNotContain(order.Name);
    }

    [TestCase(true, false, false)]
    [TestCase(false, true, false)]
    [TestCase(false, false, true)]
    public async Task APartStillInUse_IsRefused(bool hasWorks, bool hasClones, bool hasContainers)
    {
        var order = Order();
        var later = Part(ShiftStatus.SplitShift, Day.AddDays(1));
        Family(order, later);
        _familyRepository.GetUsageAsync(Arg.Is<IReadOnlyList<Guid>>(ids => ids.Contains(later.Id)), Arg.Any<CancellationToken>())
            .Returns(new OrderFamilyUsage([later.Id], hasWorks, hasClones, hasContainers));

        await RefusedAsync(order, Day, ShiftEndDateRefusedException.RowsInUseReason);
    }

    [Test]
    public async Task ARefusal_DoesNotRunTheSaveOfTheAddressedRow()
    {
        var order = Order();
        Family(order);
        _familyRepository.FindLastWorkDateAfterAsync(OrderId, Day, Arg.Any<CancellationToken>()).Returns(Day.AddDays(1));
        var saved = false;

        await Should.ThrowAsync<ShiftEndDateRefusedException>(() =>
            _service.EndFamilyAsync(order, Day, () => { saved = true; return Task.CompletedTask; }, CancellationToken.None));

        saved.ShouldBeFalse();
    }

    [Test]
    public async Task ARowPointingAtAMissingOrder_IsRefused()
    {
        var orphan = Part(ShiftStatus.OriginalShift, Start);
        Family(orphan);

        await RefusedAsync(orphan, Day, ShiftEndDateRefusedException.OrderMissingReason);
    }

    [Test]
    public async Task ARowPointingAtARowThatIsNoSealedOrder_IsRefused()
    {
        var part = Part(ShiftStatus.SplitShift, Start);
        Family(Standalone(), part);

        await RefusedAsync(part, Day, ShiftEndDateRefusedException.OrderMissingReason);
    }

    [Test]
    public async Task ARowStandingAloneWithoutAnOrder_GetsTheEndDate_AndIsLockedAsItsOwnHead()
    {
        var standalone = Standalone();
        Family(standalone);

        await _service.EndFamilyAsync(standalone, Day, null, CancellationToken.None);

        standalone.UntilDate.ShouldBe(Day);
        await _familyRepository.Received(1).LockHeadAsync(standalone.Id, Arg.Any<CancellationToken>());
        await _shiftRepository.DidNotReceiveWithAnyArgs().DeleteOrderFamilyAsync(default!, default);
    }

    [Test]
    public async Task ARowStandingAloneWithAWorkAfterTheDay_IsRefused()
    {
        var standalone = Standalone();
        Family(standalone);
        _familyRepository.FindLastWorkDateAfterAsync(standalone.Id, Day, Arg.Any<CancellationToken>()).Returns(new DateOnly(2026, 4, 2));

        await RefusedAsync(standalone, Day, ShiftEndDateRefusedException.WorksAfterReason);

        standalone.UntilDate.ShouldBeNull();
    }

    [Test]
    public async Task ARowStandingAloneThatAlreadyEnds_KeepsItsEnd()
    {
        var stale = Standalone();
        Family(Standalone(Day.AddDays(5)));

        await RefusedAsync(stale, Day, ShiftEndDateRefusedException.StoredEndReason);
    }
}
