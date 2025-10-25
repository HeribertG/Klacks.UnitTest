using AutoMapper;
using FluentAssertions;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Services.Shifts;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace UnitTest.Services.Shifts;

[TestFixture]
public class ShiftResetServiceTests
{
    private IShiftRepository _shiftRepository = null!;
    private IMapper _mapper = null!;
    private ILogger<ShiftResetService> _logger = null!;
    private ShiftResetService _service = null!;

    [SetUp]
    public void Setup()
    {
        _shiftRepository = Substitute.For<IShiftRepository>();
        _mapper = Substitute.For<IMapper>();
        _logger = Substitute.For<ILogger<ShiftResetService>>();

        _service = new ShiftResetService(
            _shiftRepository,
            _mapper,
            _logger
        );
    }

    [Test]
    public async Task CreateNewOriginalShiftFromSealedOrderAsync_ShouldCreateNewOriginalShift()
    {
        // Arrange
        var sealedOrderId = Guid.NewGuid();
        var newStartDate = new DateOnly(2025, 1, 15);
        var sealedOrder = new Shift
        {
            Id = sealedOrderId,
            Name = "Test Shift",
            Status = ShiftStatus.SealedOrder,
            FromDate = new DateOnly(2025, 1, 1),
            UntilDate = new DateOnly(2025, 12, 31),
            StartShift = TimeOnly.FromTimeSpan(TimeSpan.FromHours(8)),
            EndShift = TimeOnly.FromTimeSpan(TimeSpan.FromHours(16)),
            GroupItems = new List<GroupItem>()
        };

        var mappedShift = new Shift
        {
            Name = sealedOrder.Name,
            StartShift = sealedOrder.StartShift,
            EndShift = sealedOrder.EndShift,
            UntilDate = sealedOrder.UntilDate,
            GroupItems = new List<GroupItem>()
        };

        _mapper.Map<Shift>(sealedOrder).Returns(mappedShift);

        // Act
        var result = await _service.CreateNewOriginalShiftFromSealedOrderAsync(sealedOrder, newStartDate);

        // Assert
        result.Should().NotBeNull();
        result.Status.Should().Be(ShiftStatus.OriginalShift);
        result.FromDate.Should().Be(newStartDate);
        result.UntilDate.Should().Be(sealedOrder.UntilDate);
        result.ParentId.Should().BeNull();
        result.RootId.Should().BeNull();
        result.Lft.Should().BeNull();
        result.Rgt.Should().BeNull();

        await _shiftRepository.Received(1).Add(Arg.Is<Shift>(s => s.Status == ShiftStatus.OriginalShift));
    }

    [Test]
    public async Task CreateNewOriginalShiftFromSealedOrderAsync_ShouldSetOriginalIdToSealedOrderId()
    {
        // Arrange
        var sealedOrderId = Guid.NewGuid();
        var newStartDate = new DateOnly(2025, 1, 15);
        var sealedOrder = new Shift
        {
            Id = sealedOrderId,
            Name = "Test Shift",
            Status = ShiftStatus.SealedOrder,
            FromDate = new DateOnly(2025, 1, 1),
            UntilDate = new DateOnly(2025, 12, 31),
            GroupItems = new List<GroupItem>()
        };

        var mappedShift = new Shift
        {
            Name = sealedOrder.Name,
            GroupItems = new List<GroupItem>()
        };

        _mapper.Map<Shift>(sealedOrder).Returns(mappedShift);

        // Act
        var result = await _service.CreateNewOriginalShiftFromSealedOrderAsync(sealedOrder, newStartDate);

        // Assert
        result.OriginalId.Should().Be(sealedOrderId);
    }

    [Test]
    public async Task CreateNewOriginalShiftFromSealedOrderAsync_ShouldPreserveGroupItems()
    {
        // Arrange
        var sealedOrderId = Guid.NewGuid();
        var newStartDate = new DateOnly(2025, 1, 15);
        var groupItem = new GroupItem
        {
            GroupId = Guid.NewGuid(),
            ShiftId = sealedOrderId
        };

        var sealedOrder = new Shift
        {
            Id = sealedOrderId,
            Name = "Test Shift",
            Status = ShiftStatus.SealedOrder,
            FromDate = new DateOnly(2025, 1, 1),
            UntilDate = new DateOnly(2025, 12, 31),
            GroupItems = new List<GroupItem> { groupItem }
        };

        var mappedShift = new Shift
        {
            Name = sealedOrder.Name,
            GroupItems = new List<GroupItem> { groupItem }
        };

        _mapper.Map<Shift>(sealedOrder).Returns(mappedShift);

        // Act
        var result = await _service.CreateNewOriginalShiftFromSealedOrderAsync(sealedOrder, newStartDate);

        // Assert
        result.GroupItems.Should().HaveCount(1);
    }

    [Test]
    public void CloseExistingSplitShifts_ShouldCloseOnlySplitShifts()
    {
        // Arrange
        var closeDate = new DateOnly(2025, 1, 14);
        var splitShift1 = new Shift
        {
            Id = Guid.NewGuid(),
            Status = ShiftStatus.SplitShift,
            UntilDate = new DateOnly(2025, 12, 31)
        };
        var splitShift2 = new Shift
        {
            Id = Guid.NewGuid(),
            Status = ShiftStatus.SplitShift,
            UntilDate = new DateOnly(2025, 12, 31)
        };
        var originalShift = new Shift
        {
            Id = Guid.NewGuid(),
            Status = ShiftStatus.OriginalShift,
            UntilDate = new DateOnly(2025, 12, 31)
        };

        var shifts = new List<Shift> { splitShift1, splitShift2, originalShift };

        // Act
        _service.CloseExistingSplitShifts(shifts, closeDate);

        // Assert
        splitShift1.UntilDate.Should().Be(closeDate);
        splitShift2.UntilDate.Should().Be(closeDate);
        originalShift.UntilDate.Should().Be(new DateOnly(2025, 12, 31));
    }

    [Test]
    public void CloseExistingSplitShifts_ShouldNotCloseOriginalShifts()
    {
        // Arrange
        var closeDate = new DateOnly(2025, 1, 14);
        var originalUntilDate = new DateOnly(2025, 12, 31);
        var originalShift = new Shift
        {
            Id = Guid.NewGuid(),
            Status = ShiftStatus.OriginalShift,
            UntilDate = originalUntilDate
        };

        var shifts = new List<Shift> { originalShift };

        // Act
        _service.CloseExistingSplitShifts(shifts, closeDate);

        // Assert
        originalShift.UntilDate.Should().Be(originalUntilDate);
    }

    [Test]
    public void CloseExistingSplitShifts_ShouldNotCloseSealedOrders()
    {
        // Arrange
        var closeDate = new DateOnly(2025, 1, 14);
        var originalUntilDate = new DateOnly(2025, 12, 31);
        var sealedOrder = new Shift
        {
            Id = Guid.NewGuid(),
            Status = ShiftStatus.SealedOrder,
            UntilDate = originalUntilDate
        };

        var shifts = new List<Shift> { sealedOrder };

        // Act
        _service.CloseExistingSplitShifts(shifts, closeDate);

        // Assert
        sealedOrder.UntilDate.Should().Be(originalUntilDate);
    }

    [Test]
    public void CloseExistingSplitShifts_WithEmptyList_ShouldNotThrow()
    {
        // Arrange
        var closeDate = new DateOnly(2025, 1, 14);
        var shifts = new List<Shift>();

        // Act
        Action act = () => _service.CloseExistingSplitShifts(shifts, closeDate);

        // Assert
        act.Should().NotThrow();
    }

    [Test]
    public void CloseExistingSplitShifts_WithMixedStatuses_ShouldCloseOnlySplitShifts()
    {
        // Arrange
        var closeDate = new DateOnly(2025, 1, 14);
        var splitShift = new Shift
        {
            Id = Guid.NewGuid(),
            Status = ShiftStatus.SplitShift,
            UntilDate = new DateOnly(2025, 12, 31)
        };
        var originalShift = new Shift
        {
            Id = Guid.NewGuid(),
            Status = ShiftStatus.OriginalShift,
            UntilDate = new DateOnly(2025, 12, 31)
        };
        var sealedOrder = new Shift
        {
            Id = Guid.NewGuid(),
            Status = ShiftStatus.SealedOrder,
            UntilDate = new DateOnly(2025, 12, 31)
        };

        var shifts = new List<Shift> { splitShift, originalShift, sealedOrder };

        // Act
        _service.CloseExistingSplitShifts(shifts, closeDate);

        // Assert
        splitShift.UntilDate.Should().Be(closeDate);
        originalShift.UntilDate.Should().Be(new DateOnly(2025, 12, 31));
        sealedOrder.UntilDate.Should().Be(new DateOnly(2025, 12, 31));
    }
}
