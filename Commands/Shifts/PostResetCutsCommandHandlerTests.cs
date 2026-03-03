using FluentAssertions;
using Klacks.Api.Application.Commands.Shifts;
using Klacks.Api.Application.Handlers.Shifts;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Application.DTOs.Schedules;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Klacks.UnitTest.Commands.Shifts;

[TestFixture]
public class PostResetCutsCommandHandlerTests
{
    private IShiftCutFacade _shiftCutFacade = null!;
    private ScheduleMapper _mapper = null!;
    private ILogger<PostResetCutsCommandHandler> _logger = null!;
    private PostResetCutsCommandHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        _shiftCutFacade = Substitute.For<IShiftCutFacade>();
        _mapper = new ScheduleMapper();
        _logger = Substitute.For<ILogger<PostResetCutsCommandHandler>>();

        _handler = new PostResetCutsCommandHandler(
            _shiftCutFacade,
            _mapper,
            _logger
        );
    }

    [Test]
    public async Task Handle_WithValidRequest_ShouldResetCutsSuccessfully()
    {
        // Arrange
        var originalId = Guid.NewGuid();
        var newStartDate = new DateOnly(2025, 1, 15);

        var splitShift1 = new Shift
        {
            Id = Guid.NewGuid(),
            Status = ShiftStatus.SplitShift,
            OriginalId = originalId,
            FromDate = new DateOnly(2025, 1, 1),
            UntilDate = new DateOnly(2025, 1, 14),
            GroupItems = new List<GroupItem>()
        };

        var splitShift2 = new Shift
        {
            Id = Guid.NewGuid(),
            Status = ShiftStatus.SplitShift,
            OriginalId = originalId,
            FromDate = new DateOnly(2025, 1, 8),
            UntilDate = new DateOnly(2025, 1, 14),
            GroupItems = new List<GroupItem>()
        };

        var newOriginalShift = new Shift
        {
            Id = Guid.NewGuid(),
            OriginalId = originalId,
            Status = ShiftStatus.OriginalShift,
            FromDate = newStartDate,
            UntilDate = new DateOnly(2025, 12, 31),
            GroupItems = new List<GroupItem>()
        };

        var updatedShifts = new List<Shift> { splitShift1, splitShift2, newOriginalShift };

        _shiftCutFacade.ResetCutsAsync(originalId, newStartDate)
            .Returns(Task.FromResult(updatedShifts));

        var command = new PostResetCutsCommand(originalId, newStartDate);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Should().HaveCount(3);

        await _shiftCutFacade.Received(1).ResetCutsAsync(originalId, newStartDate);
    }

    [Test]
    public async Task Handle_NoSealedOrderFound_ShouldThrowInvalidOperationException()
    {
        // Arrange
        var originalId = Guid.NewGuid();
        var newStartDate = new DateOnly(2025, 1, 15);

        _shiftCutFacade.ResetCutsAsync(originalId, newStartDate)
            .Returns<List<Shift>>(x => throw new InvalidOperationException($"No SealedOrder found for OriginalId {originalId}"));

        var command = new PostResetCutsCommand(originalId, newStartDate);

        // Act
        Func<Task> act = async () => await _handler.Handle(command, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"No SealedOrder found for OriginalId {originalId}");
    }

    [Test]
    public async Task Handle_NoExistingShifts_ShouldReturnEmptyList()
    {
        // Arrange
        var originalId = Guid.NewGuid();
        var newStartDate = new DateOnly(2025, 1, 15);

        _shiftCutFacade.ResetCutsAsync(originalId, newStartDate)
            .Returns(Task.FromResult(new List<Shift>()));

        var command = new PostResetCutsCommand(originalId, newStartDate);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Should().BeEmpty();

        await _shiftCutFacade.Received(1).ResetCutsAsync(originalId, newStartDate);
    }
}
