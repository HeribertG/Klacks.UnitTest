using Shouldly;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Services.Shifts;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Klacks.UnitTest.Services.Shifts;

[TestFixture]
public class ShiftResetServiceTests
{
    private IShiftRepository _shiftRepository = null!;
    private ILogger<ShiftResetService> _logger = null!;
    private ShiftResetService _service = null!;

    [SetUp]
    public void Setup()
    {
        _shiftRepository = Substitute.For<IShiftRepository>();
        _logger = Substitute.For<ILogger<ShiftResetService>>();

        _service = new ShiftResetService(
            _shiftRepository,
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

        // Act
        var result = await _service.CreateNewOriginalShiftFromSealedOrderAsync(sealedOrder, newStartDate, []);

        // Assert
        result.ShouldNotBeNull();
        result.Status.ShouldBe(ShiftStatus.OriginalShift);
        result.FromDate.ShouldBe(newStartDate);
        result.UntilDate.ShouldBe(sealedOrder.UntilDate);
        result.ParentId.ShouldBeNull();
        result.RootId.ShouldBeNull();
        result.Lft.ShouldBeNull();
        result.Rgt.ShouldBeNull();

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

        // Act
        var result = await _service.CreateNewOriginalShiftFromSealedOrderAsync(sealedOrder, newStartDate, []);

        // Assert
        result.OriginalId.ShouldBe(sealedOrderId);
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

        // Act
        var result = await _service.CreateNewOriginalShiftFromSealedOrderAsync(sealedOrder, newStartDate, []);

        // Assert
        result.GroupItems.Count().ShouldBe(1);
    }

    [Test]
    public async Task CreateNewOriginalShiftFromSealedOrderAsync_TakesFreshCopiesOfTheStoredExpenses()
    {
        var stored = new ShiftExpenses { Id = Guid.NewGuid(), ShiftId = Guid.NewGuid(), Amount = 12.5m, Description = "Parking", Taxable = true };
        var sealedOrder = new Shift { Id = stored.ShiftId, Status = ShiftStatus.SealedOrder, FromDate = new DateOnly(2025, 1, 1) };

        var result = await _service.CreateNewOriginalShiftFromSealedOrderAsync(sealedOrder, new DateOnly(2025, 1, 15), [stored]);

        var copy = result.ShiftExpenses.ShouldHaveSingleItem();
        result.Id.ShouldNotBe(Guid.Empty);
        copy.Id.ShouldNotBe(stored.Id);
        copy.ShiftId.ShouldBe(result.Id);
        (copy.Amount, copy.Description, copy.Taxable).ShouldBe((12.5m, "Parking", true));
    }
}
