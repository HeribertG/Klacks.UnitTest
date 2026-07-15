// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the Shift PutCommandHandler: a null MacroId on update must never be backfilled
/// with a default — that would silently reintroduce surcharges a planner deliberately opted out of.
/// </summary>

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Handlers.Shifts;
using Klacks.Api.Application.Mappers;
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

        _handler = new PutCommandHandler(_shiftRepository, _mapper, _unitOfWork, _logger);

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
}
