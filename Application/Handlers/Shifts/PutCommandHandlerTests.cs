// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for the Shift PutCommandHandler: a null MacroId on update must never be backfilled
/// with a default — that would silently reintroduce surcharges a planner deliberately opted out of. A stored draft order
/// may only be saved as a draft or sealed; every other status is refused with 400 before anything is written.
/// </summary>

using Klacks.UnitTest.TestHelpers;
using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Handlers.Shifts;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Application.Services.Shifts;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.Shifts;

[TestFixture]
public class PutCommandHandlerTests
{
    private IShiftRepository _shiftRepository = null!;
    private ScheduleMapper _mapper = null!;
    private IUnitOfWork _unitOfWork = null!;
    private ILogger<PutCommandHandler> _logger = null!;
    private PutCommandHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        _shiftRepository = Substitute.For<IShiftRepository>();
        _mapper = new ScheduleMapper();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _logger = Substitute.For<ILogger<PutCommandHandler>>();

        _handler = new PutCommandHandler(_shiftRepository, Substitute.For<Klacks.Api.Domain.Interfaces.Associations.IShiftRequiredQualificationRepository>(), _mapper, _unitOfWork, ShiftVisibilityGuards.AllowAll(), Substitute.For<IClientReferenceGuard>(), _logger);

        _shiftRepository.PutWithSealedOrderHandling(Arg.Any<Shift>())
            .Returns(ci => ci.Arg<Shift>());
        _shiftRepository.Get(Arg.Any<Guid>()).Returns((Shift?)null);
    }

    [Test]
    public async Task Handle_WithNullMacroId_LeavesItNull()
    {
        var resource = new ShiftResource { Id = Guid.NewGuid(), Name = "Early shift", MacroId = null };

        var result = await _handler.Handle(new PutCommand<ShiftResource>(resource), CancellationToken.None);

        result!.MacroId.ShouldBeNull();
    }

    private static IEnumerable<ShiftStatus> ForbiddenDraftTargets() =>
        Enum.GetValues<ShiftStatus>().Where(status => status is not (ShiftStatus.OriginalOrder or ShiftStatus.SealedOrder));

    [TestCaseSource(nameof(ForbiddenDraftTargets))]
    public async Task Handle_DraftOrderSentWithAnotherStatus_IsRefusedWith400_NothingWritten(ShiftStatus sentStatus)
    {
        var draft = StoredDraft();
        var resource = new ShiftResource { Id = draft.Id, Name = draft.Name, Status = sentStatus };

        var ex = await Should.ThrowAsync<BadRequestException>(
            () => _handler.Handle(new PutCommand<ShiftResource>(resource), CancellationToken.None));

        ex.Message.ShouldBe(string.Format(ShiftHierarchyMessages.DraftStatusChangeNotAllowed, draft.Id, sentStatus));
        await _shiftRepository.DidNotReceiveWithAnyArgs().PutWithSealedOrderHandling(default!);
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [TestCase(ShiftStatus.OriginalOrder)]
    [TestCase(ShiftStatus.SealedOrder)]
    public async Task Handle_DraftOrderSavedAsDraftOrSealed_IsSaved(ShiftStatus sentStatus)
    {
        var draft = StoredDraft();
        var resource = new ShiftResource { Id = draft.Id, Name = draft.Name, Status = sentStatus };

        var result = await _handler.Handle(new PutCommand<ShiftResource>(resource), CancellationToken.None);

        result.ShouldNotBeNull();
        await _shiftRepository.Received(1).PutWithSealedOrderHandling(Arg.Is<Shift>(s => s.Status == sentStatus));
    }

    private Shift StoredDraft()
    {
        var draft = new Shift { Id = Guid.NewGuid(), Name = "Draft order", Status = ShiftStatus.OriginalOrder };
        _shiftRepository.GetNoTracking(draft.Id).Returns(draft);
        return draft;
    }
}
