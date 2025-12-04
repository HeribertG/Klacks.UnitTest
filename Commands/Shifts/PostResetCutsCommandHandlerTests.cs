using FluentAssertions;
using Klacks.Api.Application.Commands.Shifts;
using Klacks.Api.Application.Handlers.Shifts;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Presentation.DTOs.Schedules;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace UnitTest.Commands.Shifts;

[TestFixture]
public class PostResetCutsCommandHandlerTests
{
    private IShiftRepository _shiftRepository = null!;
    private IShiftResetService _shiftResetService = null!;
    private ScheduleMapper _mapper = null!;
    private IUnitOfWork _unitOfWork = null!;
    private ILogger<PostResetCutsCommandHandler> _logger = null!;
    private PostResetCutsCommandHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        _shiftRepository = Substitute.For<IShiftRepository>();
        _shiftResetService = Substitute.For<IShiftResetService>();
        _mapper = new ScheduleMapper();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _logger = Substitute.For<ILogger<PostResetCutsCommandHandler>>();

        _handler = new PostResetCutsCommandHandler(
            _shiftRepository,
            _shiftResetService,
            _mapper,
            _unitOfWork,
            _logger
        );
    }

    [Test]
    public async Task Handle_WithValidRequest_ShouldResetCutsSuccessfully()
    {
        // Arrange
        var originalId = Guid.NewGuid();
        var newStartDate = new DateOnly(2025, 1, 15);
        var sealedOrderId = Guid.NewGuid();

        var sealedOrder = new Shift
        {
            Id = sealedOrderId,
            Name = "Test Sealed Order",
            Status = ShiftStatus.SealedOrder,
            FromDate = new DateOnly(2025, 1, 1),
            UntilDate = new DateOnly(2025, 12, 31),
            GroupItems = new List<GroupItem>()
        };

        var splitShift1 = new Shift
        {
            Id = Guid.NewGuid(),
            Status = ShiftStatus.SplitShift,
            OriginalId = originalId,
            FromDate = new DateOnly(2025, 1, 1),
            UntilDate = new DateOnly(2025, 12, 31)
        };

        var splitShift2 = new Shift
        {
            Id = Guid.NewGuid(),
            Status = ShiftStatus.SplitShift,
            OriginalId = originalId,
            FromDate = new DateOnly(2025, 1, 8),
            UntilDate = new DateOnly(2025, 12, 31)
        };

        var newOriginalShift = new Shift
        {
            Id = Guid.NewGuid(),
            OriginalId = sealedOrderId,
            Status = ShiftStatus.OriginalShift,
            FromDate = newStartDate,
            UntilDate = sealedOrder.UntilDate,
            GroupItems = new List<GroupItem>()
        };

        var allShifts = new List<Shift> { splitShift1, splitShift2 };
        var updatedShifts = new List<Shift> { splitShift1, splitShift2, newOriginalShift };

        _shiftRepository.GetSealedOrder(originalId).Returns(Task.FromResult(sealedOrder)!);
        _shiftRepository.CutList(originalId, newStartDate, tracked: true).Returns(Task.FromResult(allShifts));
        _shiftResetService.CreateNewOriginalShiftFromSealedOrderAsync(sealedOrder, newStartDate)
            .Returns(Task.FromResult(newOriginalShift));
        _shiftRepository.CutList(originalId).Returns(Task.FromResult(updatedShifts));

        var command = new PostResetCutsCommand(originalId, newStartDate);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Should().HaveCount(3);

        await _shiftRepository.Received(1).GetSealedOrder(originalId);
        _shiftResetService.Received(1).CloseExistingSplitShifts(allShifts, newStartDate.AddDays(-1));
        await _shiftResetService.Received(1).CreateNewOriginalShiftFromSealedOrderAsync(sealedOrder, newStartDate);
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task Handle_NoSealedOrderFound_ShouldThrowInvalidOperationException()
    {
        // Arrange
        var originalId = Guid.NewGuid();
        var newStartDate = new DateOnly(2025, 1, 15);

        _shiftRepository.GetSealedOrder(originalId).Returns(Task.FromResult<Shift>(null!));

        var command = new PostResetCutsCommand(originalId, newStartDate);

        // Act
        Func<Task> act = async () => await _handler.Handle(command, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"No SealedOrder found for OriginalId {originalId}");

        await _shiftResetService.DidNotReceive().CreateNewOriginalShiftFromSealedOrderAsync(Arg.Any<Shift>(), Arg.Any<DateOnly>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Handle_NoExistingShifts_ShouldReturnEmptyList()
    {
        // Arrange
        var originalId = Guid.NewGuid();
        var newStartDate = new DateOnly(2025, 1, 15);

        var sealedOrder = new Shift
        {
            Id = Guid.NewGuid(),
            Name = "Test Sealed Order",
            Status = ShiftStatus.SealedOrder,
            FromDate = new DateOnly(2025, 1, 1),
            UntilDate = new DateOnly(2025, 12, 31),
            GroupItems = new List<GroupItem>()
        };

        _shiftRepository.GetSealedOrder(originalId).Returns(Task.FromResult(sealedOrder)!);
        _shiftRepository.CutList(originalId, newStartDate, tracked: true).Returns(Task.FromResult(new List<Shift>()));

        var command = new PostResetCutsCommand(originalId, newStartDate);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Should().BeEmpty();

        _shiftResetService.DidNotReceive().CloseExistingSplitShifts(Arg.Any<List<Shift>>(), Arg.Any<DateOnly>());
        await _shiftResetService.DidNotReceive().CreateNewOriginalShiftFromSealedOrderAsync(Arg.Any<Shift>(), Arg.Any<DateOnly>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Handle_ShouldCloseShiftsBeforeNewStartDate()
    {
        // Arrange
        var originalId = Guid.NewGuid();
        var newStartDate = new DateOnly(2025, 1, 15);
        var expectedCloseDate = new DateOnly(2025, 1, 14);

        var sealedOrder = new Shift
        {
            Id = Guid.NewGuid(),
            Name = "Test Sealed Order",
            Status = ShiftStatus.SealedOrder,
            GroupItems = new List<GroupItem>()
        };

        var splitShift = new Shift
        {
            Id = Guid.NewGuid(),
            Status = ShiftStatus.SplitShift,
            OriginalId = originalId
        };

        var allShifts = new List<Shift> { splitShift };
        var newOriginalShift = new Shift
        {
            Id = Guid.NewGuid(),
            Status = ShiftStatus.OriginalShift,
            GroupItems = new List<GroupItem>()
        };

        _shiftRepository.GetSealedOrder(originalId).Returns(Task.FromResult(sealedOrder)!);
        _shiftRepository.CutList(originalId, newStartDate, tracked: true).Returns(Task.FromResult(allShifts));
        _shiftResetService.CreateNewOriginalShiftFromSealedOrderAsync(sealedOrder, newStartDate)
            .Returns(Task.FromResult(newOriginalShift));
        _shiftRepository.CutList(originalId).Returns(Task.FromResult(new List<Shift> { newOriginalShift }));

        var command = new PostResetCutsCommand(originalId, newStartDate);

        // Act
        await _handler.Handle(command, CancellationToken.None);

        // Assert
        _shiftResetService.Received(1).CloseExistingSplitShifts(
            Arg.Is<List<Shift>>(list => list.Count == 1 && list[0].Id == splitShift.Id),
            expectedCloseDate
        );
    }

    [Test]
    public async Task Handle_ShouldCreateNewOriginalShiftWithCorrectParameters()
    {
        // Arrange
        var originalId = Guid.NewGuid();
        var newStartDate = new DateOnly(2025, 1, 15);

        var sealedOrder = new Shift
        {
            Id = Guid.NewGuid(),
            Name = "Test Sealed Order",
            Status = ShiftStatus.SealedOrder,
            GroupItems = new List<GroupItem>()
        };

        var splitShift = new Shift
        {
            Id = Guid.NewGuid(),
            Status = ShiftStatus.SplitShift
        };

        var newOriginalShift = new Shift
        {
            Id = Guid.NewGuid(),
            Status = ShiftStatus.OriginalShift,
            GroupItems = new List<GroupItem>()
        };

        _shiftRepository.GetSealedOrder(originalId).Returns(Task.FromResult(sealedOrder)!);
        _shiftRepository.CutList(originalId, newStartDate, tracked: true).Returns(Task.FromResult(new List<Shift> { splitShift }));
        _shiftResetService.CreateNewOriginalShiftFromSealedOrderAsync(sealedOrder, newStartDate)
            .Returns(Task.FromResult(newOriginalShift));
        _shiftRepository.CutList(originalId).Returns(Task.FromResult(new List<Shift> { newOriginalShift }));

        var command = new PostResetCutsCommand(originalId, newStartDate);

        // Act
        await _handler.Handle(command, CancellationToken.None);

        // Assert
        await _shiftResetService.Received(1).CreateNewOriginalShiftFromSealedOrderAsync(
            Arg.Is<Shift>(s => s.Id == sealedOrder.Id),
            newStartDate
        );
    }
}
