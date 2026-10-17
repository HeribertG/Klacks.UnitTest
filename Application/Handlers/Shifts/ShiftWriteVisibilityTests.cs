// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Shift write paths answer a shift the group-restricted caller may not see exactly like a missing one and write
/// nothing: PUT (null), required-qualification set/delete/get, reset date range, order sealing, linking the shift into
/// a group, and the update_shift skill. The guard itself is covered by ShiftVisibilityGuardTests.
/// </summary>

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.Commands.Qualifications;
using Klacks.Api.Application.DTOs.Associations;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Handlers.Qualifications;
using Klacks.Api.Application.Handlers.Shifts;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Application.Queries.Qualifications;
using Klacks.Api.Application.Queries.Shifts;
using Klacks.Api.Application.Services.Orders;
using Klacks.Api.Application.Services.Shifts;
using Klacks.Api.Application.Skills;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using GroupItemPostHandler = Klacks.Api.Application.Handlers.GroupItems.PostCommandHandler;

namespace Klacks.UnitTest.Application.Handlers.Shifts;

[TestFixture]
public class ShiftWriteVisibilityTests
{
    private static readonly Guid ShiftId = Guid.NewGuid();

    private IShiftRepository _shiftRepository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private IShiftRequiredQualificationRepository _requirements = null!;

    [SetUp]
    public void SetUp()
    {
        _shiftRepository = Substitute.For<IShiftRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _requirements = Substitute.For<IShiftRequiredQualificationRepository>();
    }

    private static Shift StoredShift() => new()
    {
        Id = ShiftId,
        Name = "Hidden order",
        Status = ShiftStatus.SealedOrder,
        FromDate = new DateOnly(2026, 1, 1)
    };

    [Test]
    public async Task Put_HiddenShift_ReturnsNullLikeMissing_AndWritesNothing()
    {
        var handler = new PutCommandHandler(
            _shiftRepository, _requirements, new ScheduleMapper(), _unitOfWork, ShiftVisibilityGuards.HideAll(),
            Substitute.For<IClientReferenceGuard>(), Substitute.For<ILogger<PutCommandHandler>>());

        var result = await handler.Handle(
            new PutCommand<ShiftResource>(new ShiftResource { Id = ShiftId, Name = "Renamed" }), CancellationToken.None);

        result.ShouldBeNull();
        await _shiftRepository.DidNotReceiveWithAnyArgs().PutWithSealedOrderHandling(default!);
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Put_VisibleShift_ChecksGroupChangesBeforeWriting()
    {
        var guard = ShiftVisibilityGuards.AllowAll();
        guard.EnsureGroupChangesVisibleAsync(ShiftId, Arg.Any<IReadOnlyCollection<GroupItem>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new KeyNotFoundException("Group with ID x not found")));
        var handler = new PutCommandHandler(
            _shiftRepository, _requirements, new ScheduleMapper(), _unitOfWork, guard,
            Substitute.For<IClientReferenceGuard>(), Substitute.For<ILogger<PutCommandHandler>>());

        await Should.ThrowAsync<KeyNotFoundException>(() => handler.Handle(
            new PutCommand<ShiftResource>(new ShiftResource { Id = ShiftId, Name = "Renamed" }), CancellationToken.None));

        await _shiftRepository.DidNotReceiveWithAnyArgs().PutWithSealedOrderHandling(default!);
    }

    [Test]
    public async Task SetRequiredQualification_HiddenShift_IsNotFound_AndWritesNothing()
    {
        var handler = new SetShiftRequiredQualificationCommandHandler(_requirements, _unitOfWork, ShiftVisibilityGuards.HideAll());

        var error = await Should.ThrowAsync<KeyNotFoundException>(() => handler.Handle(
            new SetShiftRequiredQualificationCommand(ShiftId, Guid.NewGuid(), true, QualificationLevel.Advanced, null),
            CancellationToken.None));

        error.Message.ShouldBe(string.Format(ShiftVisibilityMessages.ShiftNotFound, ShiftId));
        await _requirements.DidNotReceiveWithAnyArgs().Add(default!);
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task DeleteRequiredQualification_RowOfHiddenShift_IsLeftUntouched()
    {
        var rowId = Guid.NewGuid();
        _requirements.Get(rowId).Returns(new ShiftRequiredQualification { Id = rowId, ShiftId = ShiftId });
        var handler = new DeleteShiftRequiredQualificationCommandHandler(_requirements, _unitOfWork, ShiftVisibilityGuards.HideAll());

        await handler.Handle(new DeleteShiftRequiredQualificationCommand(rowId), CancellationToken.None);

        await _requirements.DidNotReceive().Delete(rowId);
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task GetRequiredQualifications_HiddenShift_IsEmptyLikeMissing()
    {
        _requirements.GetByShiftIdAsync(ShiftId, Arg.Any<CancellationToken>())
            .Returns([new ShiftRequiredQualification { Id = Guid.NewGuid(), ShiftId = ShiftId }]);
        var handler = new GetShiftRequiredQualificationsQueryHandler(_requirements, ShiftVisibilityGuards.HideAll());

        var result = await handler.Handle(new GetShiftRequiredQualificationsQuery(ShiftId), CancellationToken.None);

        result.ShouldBeEmpty();
    }

    [Test]
    public async Task ResetDateRange_HiddenOrder_IsAnsweredLikeMissing()
    {
        _shiftRepository.GetSealedOrder(ShiftId).Returns(StoredShift());
        var handler = new GetResetDateRangeQueryHandler(
            _shiftRepository, ShiftVisibilityGuards.HideAll(), Substitute.For<ILogger<GetResetDateRangeQueryHandler>>());

        var error = await Should.ThrowAsync<KeyNotFoundException>(
            () => handler.Handle(new GetResetDateRangeQuery(ShiftId), CancellationToken.None));

        error.Message.ShouldBe(string.Format(ShiftVisibilityMessages.SealedOrderNotFound, ShiftId));
    }

    [Test]
    public async Task Sealing_HiddenOrder_IsRefusedLikeMissing()
    {
        var hidden = StoredShift();
        hidden.Status = ShiftStatus.OriginalOrder;
        _shiftRepository.Get(ShiftId).Returns(hidden);
        var hiddenOutcome = await new OrderSealingService(_shiftRepository, _unitOfWork, ShiftVisibilityGuards.HideAll())
            .SealAsync(ShiftId);

        _shiftRepository.Get(ShiftId).Returns((Shift?)null);
        var missingOutcome = await new OrderSealingService(_shiftRepository, _unitOfWork, ShiftVisibilityGuards.AllowAll())
            .SealAsync(ShiftId);

        hiddenOutcome.IsSealed.ShouldBeFalse();
        hiddenOutcome.FailureReason.ShouldBe(missingOutcome.FailureReason);
        hiddenOutcome.OrderName.ShouldBe(missingOutcome.OrderName);
        await _shiftRepository.DidNotReceiveWithAnyArgs().PutWithSealedOrderHandling(default!);
    }

    [Test]
    public async Task LinkingHiddenShiftIntoGroup_IsNotFound_AndWritesNothing()
    {
        var groupItems = Substitute.For<IGroupItemRepository>();
        var handler = new GroupItemPostHandler(
            groupItems, new GroupMapper(), _unitOfWork, TestGroupWriteVisibility.UnrestrictedGroups(),
            TestGroupWriteVisibility.AllClientsVisible(), ShiftVisibilityGuards.HideAll(),
            NullLogger<GroupItemPostHandler>.Instance);

        await Should.ThrowAsync<KeyNotFoundException>(() => handler.Handle(
            new PostCommand<GroupItemResource>(new GroupItemResource { Id = Guid.NewGuid(), GroupId = Guid.NewGuid(), ShiftId = ShiftId }),
            CancellationToken.None));

        await groupItems.DidNotReceiveWithAnyArgs().Add(default!);
    }

    [Test]
    public async Task UpdateShiftSkill_HiddenShift_AnswersExactlyLikeMissing()
    {
        var mediator = Substitute.For<IMediator>();
        _shiftRepository.Get(ShiftId).Returns(StoredShift());
        var hidden = await new UpdateShiftSkill(_shiftRepository, new ScheduleMapper(), mediator, ShiftVisibilityGuards.HideAll())
            .ExecuteAsync(Ctx(), new Dictionary<string, object> { ["shiftId"] = ShiftId.ToString(), ["name"] = "X" });

        _shiftRepository.Get(ShiftId).Returns((Shift?)null);
        var missing = await new UpdateShiftSkill(_shiftRepository, new ScheduleMapper(), mediator, ShiftVisibilityGuards.AllowAll())
            .ExecuteAsync(Ctx(), new Dictionary<string, object> { ["shiftId"] = ShiftId.ToString(), ["name"] = "X" });

        hidden.Success.ShouldBeFalse();
        hidden.Message.ShouldBe(missing.Message);
        await mediator.DidNotReceiveWithAnyArgs().Send(Arg.Any<PutCommand<ShiftResource>>(), Arg.Any<CancellationToken>());
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanEditShifts" }
    };
}
