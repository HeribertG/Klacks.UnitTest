using FluentAssertions;
using Klacks.Api.Application.Handlers.Shifts;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Queries.Shifts;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Schedules;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace UnitTest.Queries.Shifts;

[TestFixture]
public class GetResetDateRangeQueryHandlerTests
{
    private IShiftRepository _shiftRepository = null!;
    private ILogger<GetResetDateRangeQueryHandler> _logger = null!;
    private GetResetDateRangeQueryHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        _shiftRepository = Substitute.For<IShiftRepository>();
        _logger = Substitute.For<ILogger<GetResetDateRangeQueryHandler>>();

        _handler = new GetResetDateRangeQueryHandler(
            _shiftRepository,
            _logger
        );
    }

    [Test]
    public async Task Handle_WithValidOriginalId_ShouldReturnCorrectDateRange()
    {
        // Arrange
        var originalId = Guid.NewGuid();
        var fromDate = new DateOnly(2025, 1, 1);
        var untilDate = new DateOnly(2025, 12, 31);

        var sealedOrder = new Shift
        {
            Id = Guid.NewGuid(),
            Name = "Test Sealed Order",
            Status = ShiftStatus.SealedOrder,
            FromDate = fromDate,
            UntilDate = untilDate
        };

        _shiftRepository.GetSealedOrder(originalId).Returns(Task.FromResult(sealedOrder)!);

        var query = new GetResetDateRangeQuery(originalId);

        // Act
        var result = await _handler.Handle(query, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.EarliestResetDate.Should().Be(fromDate);
        result.UntilDate.Should().Be(untilDate);

        await _shiftRepository.Received(1).GetSealedOrder(originalId);
    }

    [Test]
    public async Task Handle_WithNullUntilDate_ShouldReturnNullUntilDate()
    {
        // Arrange
        var originalId = Guid.NewGuid();
        var fromDate = new DateOnly(2025, 1, 1);

        var sealedOrder = new Shift
        {
            Id = Guid.NewGuid(),
            Name = "Test Sealed Order",
            Status = ShiftStatus.SealedOrder,
            FromDate = fromDate,
            UntilDate = null
        };

        _shiftRepository.GetSealedOrder(originalId).Returns(Task.FromResult(sealedOrder)!);

        var query = new GetResetDateRangeQuery(originalId);

        // Act
        var result = await _handler.Handle(query, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.EarliestResetDate.Should().Be(fromDate);
        result.UntilDate.Should().BeNull();
    }

    [Test]
    public async Task Handle_NoSealedOrderFound_ShouldThrowKeyNotFoundException()
    {
        // Arrange
        var originalId = Guid.NewGuid();

        _shiftRepository.GetSealedOrder(originalId).Returns(Task.FromResult<Shift>(null!));

        var query = new GetResetDateRangeQuery(originalId);

        // Act
        Func<Task> act = async () => await _handler.Handle(query, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<KeyNotFoundException>()
            .WithMessage($"SealedOrder with OriginalId {originalId} not found");
    }


    [Test]
    public async Task Handle_EarliestResetDateEqualsFromDate_AsDocumented()
    {
        // Arrange
        var originalId = Guid.NewGuid();
        var fromDate = new DateOnly(2025, 6, 15);

        var sealedOrder = new Shift
        {
            Id = Guid.NewGuid(),
            Status = ShiftStatus.SealedOrder,
            FromDate = fromDate,
            UntilDate = new DateOnly(2025, 12, 31)
        };

        _shiftRepository.GetSealedOrder(originalId).Returns(Task.FromResult(sealedOrder)!);

        var query = new GetResetDateRangeQuery(originalId);

        // Act
        var result = await _handler.Handle(query, CancellationToken.None);

        // Assert
        result.EarliestResetDate.Should().Be(fromDate);
    }
}
