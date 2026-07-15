// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the Shift PostCommandHandler: applies the resolved default macro only when the
/// caller left MacroId empty, and never overrides an explicitly supplied macro.
/// </summary>

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Handlers.Shifts;
using Klacks.Api.Application.Mappers;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.Shifts;

[TestFixture]
public class PostCommandHandlerTests
{
    private IShiftRepository _shiftRepository = null!;
    private ScheduleMapper _mapper = null!;
    private IUnitOfWork _unitOfWork = null!;
    private IDefaultShiftMacroResolver _defaultShiftMacroResolver = null!;
    private ILogger<PostCommandHandler> _logger = null!;
    private PostCommandHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        _shiftRepository = Substitute.For<IShiftRepository>();
        _mapper = new ScheduleMapper();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _defaultShiftMacroResolver = Substitute.For<IDefaultShiftMacroResolver>();
        _logger = Substitute.For<ILogger<PostCommandHandler>>();

        _handler = new PostCommandHandler(
            _shiftRepository,
            _mapper,
            _unitOfWork,
            _defaultShiftMacroResolver,
            _logger);

        _shiftRepository.AddWithSealedOrderHandling(Arg.Any<Shift>())
            .Returns(ci => ci.Arg<Shift>());
        _shiftRepository.Get(Arg.Any<Guid>()).Returns((Shift?)null);
    }

    [Test]
    public async Task Handle_WithoutMacroId_AppliesResolvedDefaultMacro()
    {
        var defaultMacroId = Guid.NewGuid();
        _defaultShiftMacroResolver.ResolveDefaultMacroIdAsync(Arg.Any<CancellationToken>())
            .Returns(defaultMacroId);
        var resource = new ShiftResource { Name = "Early shift", MacroId = null };

        var result = await _handler.Handle(new PostCommand<ShiftResource>(resource), CancellationToken.None);

        result!.MacroId.ShouldBe(defaultMacroId);
        await _defaultShiftMacroResolver.Received(1).ResolveDefaultMacroIdAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_WithoutMacroId_AndNoDefaultConfigured_LeavesMacroIdNull()
    {
        _defaultShiftMacroResolver.ResolveDefaultMacroIdAsync(Arg.Any<CancellationToken>())
            .Returns((Guid?)null);
        var resource = new ShiftResource { Name = "Early shift", MacroId = null };

        var result = await _handler.Handle(new PostCommand<ShiftResource>(resource), CancellationToken.None);

        result!.MacroId.ShouldBeNull();
    }

    [Test]
    public async Task Handle_WithExplicitMacroId_KeepsItUntouched_AndDoesNotConsultResolver()
    {
        var explicitMacroId = Guid.NewGuid();
        var resource = new ShiftResource { Name = "Early shift", MacroId = explicitMacroId };

        var result = await _handler.Handle(new PostCommand<ShiftResource>(resource), CancellationToken.None);

        result!.MacroId.ShouldBe(explicitMacroId);
        await _defaultShiftMacroResolver.DidNotReceive().ResolveDefaultMacroIdAsync(Arg.Any<CancellationToken>());
    }
}
