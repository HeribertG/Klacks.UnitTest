// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for the lock the Shift put and delete handlers apply to shifts that are no longer draft orders: an edit that
/// changes locked content or a delete throws ShiftLockedException (a 409 with the SHIFT_LOCKED code and the locked field
/// names) before any repository write, while an unchanged save, a save that changes only editable parts, the sealing save
/// of a draft order and a missing shift go through. Without an administrator in the web request a sealed order is refused
/// as well (the administrator family delete is covered in ShiftLifecycleTests).
/// </summary>

using Klacks.UnitTest.TestHelpers;
using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Handlers.Shifts;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.Shifts;

[TestFixture]
public class ShiftLockHandlerTests
{
    private IShiftRepository _shiftRepository = null!;
    private ScheduleMapper _mapper = null!;
    private IUnitOfWork _unitOfWork = null!;
    private PutCommandHandler _putHandler = null!;
    private Klacks.Api.Application.Interfaces.IShiftFamilyEndService _shiftFamilyEndService = null!;
    private DeleteCommandHandler _deleteHandler = null!;

    [SetUp]
    public void Setup()
    {
        _shiftRepository = Substitute.For<IShiftRepository>();
        _mapper = new ScheduleMapper();
        _unitOfWork = Substitute.For<IUnitOfWork>();

        _shiftFamilyEndService = Substitute.For<Klacks.Api.Application.Interfaces.IShiftFamilyEndService>();
        _shiftFamilyEndService.EndFamilyAsync(Arg.Any<Shift>(), Arg.Any<DateOnly>(), Arg.Any<Func<Task>?>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<Func<Task>?>()!());
        _putHandler = new PutCommandHandler(
            _shiftRepository,
            Substitute.For<IShiftRequiredQualificationRepository>(),
            _mapper,
            _unitOfWork,
            ShiftVisibilityGuards.AllowAll(),
            Substitute.For<IClientReferenceGuard>(),
            Substitute.For<Klacks.Api.Application.Interfaces.IVariantShiftWriteGuard>(),
            _shiftFamilyEndService,
            Substitute.For<ILogger<PutCommandHandler>>());
        var userService = Substitute.For<Klacks.Api.Domain.Interfaces.IUserService>();
        userService.GetRights().Returns(Klacks.Api.Domain.Constants.Permissions.PlannerFloor);
        _deleteHandler = new DeleteCommandHandler(
            _shiftRepository,
            _mapper,
            _unitOfWork,
            Substitute.For<ISealedOrderFamilyDeletion>(),
            userService,
            ShiftVisibilityGuards.AllowAll(),
            Substitute.For<Klacks.Api.Application.Interfaces.IVariantShiftWriteGuard>(),
            Substitute.For<ILogger<DeleteCommandHandler>>());

        _shiftRepository.PutWithSealedOrderHandling(Arg.Any<Shift>()).Returns(call => call.Arg<Shift>());
    }

    private Shift Stored(ShiftStatus status)
    {
        var shift = new Shift
        {
            Id = Guid.NewGuid(),
            Name = "Night patrol",
            Abbreviation = "NP",
            Status = status,
            FromDate = new DateOnly(2026, 1, 1),
            StartShift = new TimeOnly(22, 0),
            EndShift = new TimeOnly(6, 0),
            IsMonday = true,
            Quantity = 1,
            SumEmployees = 1,
            WorkTime = 8m
        };
        _shiftRepository.GetNoTracking(shift.Id).Returns(shift);
        _shiftRepository.Get(shift.Id).Returns(shift);
        return shift;
    }

    [TestCase(ShiftStatus.SealedOrder)]
    [TestCase(ShiftStatus.OriginalShift)]
    [TestCase(ShiftStatus.SplitShift)]
    public async Task Put_ChangingLockedContent_ThrowsShiftLockedBeforeAnyWrite(ShiftStatus status)
    {
        var stored = Stored(status);
        var resource = _mapper.ToShiftResource(stored);
        resource.Name = "Renamed";
        resource.StartShift = new TimeOnly(21, 0);

        var thrown = await Should.ThrowAsync<ShiftLockedException>(() =>
            _putHandler.Handle(new PutCommand<ShiftResource>(resource), CancellationToken.None));

        thrown.ConflictCode.ShouldBe(ShiftLockedException.ErrorCode);
        ((string[])thrown.ConflictDetails![ShiftLockedException.LockedFieldsKey]!).ShouldBe(
            [nameof(Shift.Name), nameof(Shift.StartShift)], ignoreOrder: true);
        thrown.ConflictDetails[ShiftLockedException.ShiftIdKey].ShouldBe((object)stored.Id);
        await _shiftRepository.DidNotReceive().PutWithSealedOrderHandling(Arg.Any<Shift>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [TestCase(ShiftStatus.SealedOrder)]
    [TestCase(ShiftStatus.OriginalShift)]
    [TestCase(ShiftStatus.SplitShift)]
    public async Task Put_UnchangedRoundtrip_Passes(ShiftStatus status)
    {
        var stored = Stored(status);
        var resource = _mapper.ToShiftResource(stored);

        var result = await _putHandler.Handle(new PutCommand<ShiftResource>(resource), CancellationToken.None);

        result.ShouldNotBeNull();
        await _shiftRepository.Received(1).PutWithSealedOrderHandling(Arg.Any<Shift>());
    }

    [Test]
    public async Task Put_ChangingOnlyEditableParts_Passes()
    {
        var stored = Stored(ShiftStatus.SealedOrder);
        var resource = _mapper.ToShiftResource(stored);
        resource.MacroId = Guid.NewGuid();
        resource.UntilDate = new DateOnly(2026, 12, 31);
        resource.DefaultExpenses = [new ShiftExpensesResource { Amount = 5m, Description = "Meal" }];

        var result = await _putHandler.Handle(new PutCommand<ShiftResource>(resource), CancellationToken.None);

        result.ShouldNotBeNull();
        await _shiftRepository.Received(1).PutWithSealedOrderHandling(Arg.Any<Shift>());
        await _shiftFamilyEndService.Received(1).EndFamilyAsync(
            Arg.Is<Shift>(shift => shift.Id == stored.Id), new DateOnly(2026, 12, 31), Arg.Any<Func<Task>?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Put_WithoutAFirstEndDate_SavesTheRowAlone()
    {
        var stored = Stored(ShiftStatus.SealedOrder);
        var resource = _mapper.ToShiftResource(stored);
        resource.MacroId = Guid.NewGuid();

        await _putHandler.Handle(new PutCommand<ShiftResource>(resource), CancellationToken.None);

        await _shiftRepository.Received(1).PutWithSealedOrderHandling(Arg.Any<Shift>());
        await _shiftFamilyEndService.DidNotReceiveWithAnyArgs().EndFamilyAsync(default!, default, default, default);
    }

    [Test]
    public async Task Put_AFirstEndDateOnADraftOrder_SavesTheRowAlone()
    {
        var stored = Stored(ShiftStatus.OriginalOrder);
        var resource = _mapper.ToShiftResource(stored);
        resource.UntilDate = new DateOnly(2026, 12, 31);

        await _putHandler.Handle(new PutCommand<ShiftResource>(resource), CancellationToken.None);

        await _shiftRepository.Received(1).PutWithSealedOrderHandling(Arg.Any<Shift>());
        await _shiftFamilyEndService.DidNotReceiveWithAnyArgs().EndFamilyAsync(default!, default, default, default);
    }

    [Test]
    public async Task Put_SealingADraftOrder_Passes()
    {
        var stored = Stored(ShiftStatus.OriginalOrder);
        var resource = _mapper.ToShiftResource(stored);
        resource.Status = ShiftStatus.SealedOrder;
        resource.Name = "Renamed while sealing";

        var result = await _putHandler.Handle(new PutCommand<ShiftResource>(resource), CancellationToken.None);

        result.ShouldNotBeNull();
    }

    [Test]
    public async Task Put_ForAShiftThatDoesNotExist_StillReturnsNull()
    {
        var resource = new ShiftResource { Id = Guid.NewGuid(), Name = "Ghost" };
        _shiftRepository.GetNoTracking(resource.Id).Returns((Shift?)null);
        _shiftRepository.PutWithSealedOrderHandling(Arg.Any<Shift>()).Returns((Shift?)null);

        var result = await _putHandler.Handle(new PutCommand<ShiftResource>(resource), CancellationToken.None);

        result.ShouldBeNull();
    }

    [TestCase(ShiftStatus.SealedOrder)]
    [TestCase(ShiftStatus.OriginalShift)]
    [TestCase(ShiftStatus.SplitShift)]
    public async Task Delete_NonDraftShift_ThrowsShiftLockedBeforeAnyWrite(ShiftStatus status)
    {
        var stored = Stored(status);

        var thrown = await Should.ThrowAsync<ShiftLockedException>(() =>
            _deleteHandler.Handle(new DeleteCommand<ShiftResource>(stored.Id), CancellationToken.None));

        thrown.ConflictCode.ShouldBe(ShiftLockedException.ErrorCode);
        thrown.Message.ShouldContain(status.ToString());
        await _shiftRepository.DidNotReceive().Delete(Arg.Any<Guid>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Delete_DraftOrder_Passes()
    {
        var stored = Stored(ShiftStatus.OriginalOrder);

        var result = await _deleteHandler.Handle(new DeleteCommand<ShiftResource>(stored.Id), CancellationToken.None);

        result.ShouldNotBeNull();
        await _shiftRepository.Received(1).Delete(stored.Id);
    }
}
