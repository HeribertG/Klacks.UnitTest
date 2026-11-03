using Shouldly;
using Klacks.UnitTest.TestHelpers;
using Klacks.Api.Application.Handlers.Shifts;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Application.Queries.Shifts;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Schedules;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Klacks.UnitTest.Queries.Shifts;

[TestFixture]
public class GetResetDateRangeQueryHandlerTests
{
    private IShiftRepository _shiftRepository = null!;
    private IVariantShiftWriteGuard _variantGuard = null!;
    private IOrderFamilyEndRepository _familyRepository = null!;
    private ILogger<GetResetDateRangeQueryHandler> _logger = null!;
    private GetResetDateRangeQueryHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        _shiftRepository = Substitute.For<IShiftRepository>();
        _variantGuard = Substitute.For<IVariantShiftWriteGuard>();
        _familyRepository = Substitute.For<IOrderFamilyEndRepository>();
        _shiftRepository.CutList(Arg.Any<Guid>(), Arg.Any<DateOnly?>(), Arg.Any<bool>(), Arg.Any<Guid?>()).Returns([]);
        _logger = Substitute.For<ILogger<GetResetDateRangeQueryHandler>>();

        _handler = new GetResetDateRangeQueryHandler(
            _shiftRepository,
            ShiftVisibilityGuards.AllowAll(),
            _variantGuard,
            _familyRepository,
            _logger
        );
    }

    [Test]
    public async Task Handle_WithoutWork_TheEarliestDateIsTheStartOfTheOrder()
    {
        var originalId = Guid.NewGuid();
        var from = new DateOnly(2025, 3, 1);
        _shiftRepository.GetSealedOrder(originalId).Returns(new Shift { Id = originalId, Status = ShiftStatus.SealedOrder, FromDate = from });

        var result = await _handler.Handle(new GetResetDateRangeQuery(originalId), CancellationToken.None);

        result.EarliestResetDate.ShouldBe(from);
    }

    [Test]
    public async Task Handle_WithWork_TheEarliestDateIsTheDayAfterTheLastWork()
    {
        var originalId = Guid.NewGuid();
        _shiftRepository.GetSealedOrder(originalId)
            .Returns(new Shift { Id = originalId, Status = ShiftStatus.SealedOrder, FromDate = new DateOnly(2025, 3, 1) });
        _familyRepository.FindLastWorkDateAfterAsync(originalId, DateOnly.MinValue, CancellationToken.None)
            .Returns(new DateOnly(2025, 5, 10));

        var result = await _handler.Handle(new GetResetDateRangeQuery(originalId), CancellationToken.None);

        result.EarliestResetDate.ShouldBe(new DateOnly(2025, 5, 11));
    }

    [Test]
    public async Task Handle_WithAHiddenRow_IsAnsweredLikeAMissingOrder_AndReadsNoWork()
    {
        var originalId = Guid.NewGuid();
        var piece = new Shift { Id = Guid.NewGuid(), OriginalId = originalId, Status = ShiftStatus.SplitShift };
        _shiftRepository.GetSealedOrder(originalId).Returns(new Shift { Id = originalId, Status = ShiftStatus.SealedOrder });
        _shiftRepository.CutList(originalId, null, false, null).Returns([piece]);
        var guard = ShiftVisibilityGuards.AllowAll();
        guard.FindFirstHiddenAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>()).Returns(piece.Id);
        var handler = new GetResetDateRangeQueryHandler(_shiftRepository, guard, _variantGuard, _familyRepository, _logger);

        await Should.ThrowAsync<KeyNotFoundException>(() => handler.Handle(new GetResetDateRangeQuery(originalId), CancellationToken.None));

        await _familyRepository.DidNotReceiveWithAnyArgs().FindLastWorkDateAfterAsync(default, default, default);
    }

    [Test]
    public async Task Handle_OrderInAnOpenKey_IsFoundThroughTheStoredToken()
    {
        var originalId = Guid.NewGuid();
        var token = Guid.NewGuid();
        var sealedOrder = new Shift
        {
            Id = originalId,
            Status = ShiftStatus.SealedOrder,
            FromDate = new DateOnly(2025, 3, 1),
            UntilDate = new DateOnly(2025, 9, 30),
            AnalyseToken = token
        };
        _shiftRepository.Get(originalId).Returns(sealedOrder);
        _variantGuard.IsReadableVariantAsync(token, CancellationToken.None).Returns(true);
        _shiftRepository.GetSealedOrder(originalId, token).Returns(sealedOrder);

        var result = await _handler.Handle(new GetResetDateRangeQuery(originalId), CancellationToken.None);

        result.EarliestResetDate.ShouldBe(sealedOrder.FromDate);
        result.UntilDate.ShouldBe(sealedOrder.UntilDate);
    }

    [Test]
    public async Task Handle_OrderOfAScenarioClone_IsAnsweredLikeAMissingOrder()
    {
        var originalId = Guid.NewGuid();
        var cloneToken = Guid.NewGuid();
        _shiftRepository.Get(originalId).Returns(new Shift { Id = originalId, Status = ShiftStatus.SealedOrder, AnalyseToken = cloneToken });

        var error = await Should.ThrowAsync<KeyNotFoundException>(
            () => _handler.Handle(new GetResetDateRangeQuery(originalId), CancellationToken.None));

        error.Message.ShouldBe($"SealedOrder with OriginalId {originalId} not found");
        await _shiftRepository.DidNotReceive().GetSealedOrder(originalId, cloneToken);
    }

    [Test]
    public async Task Handle_RealOrder_NeverAsksForTheStoredShiftOrTheVariantGuard()
    {
        var originalId = Guid.NewGuid();
        _shiftRepository.GetSealedOrder(originalId).Returns(
            new Shift { Id = originalId, Status = ShiftStatus.SealedOrder, FromDate = new DateOnly(2025, 3, 1) });

        await _handler.Handle(new GetResetDateRangeQuery(originalId), CancellationToken.None);

        await _shiftRepository.DidNotReceive().Get(originalId);
        await _variantGuard.DidNotReceiveWithAnyArgs().IsReadableVariantAsync(default, default);
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

        _shiftRepository.GetSealedOrder(originalId).Returns(Task.FromResult<Shift?>(sealedOrder));

        var query = new GetResetDateRangeQuery(originalId);

        // Act
        var result = await _handler.Handle(query, CancellationToken.None);

        // Assert
        result.ShouldNotBeNull();
        result.EarliestResetDate.ShouldBe(fromDate);
        result.UntilDate.ShouldBe(untilDate);

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

        _shiftRepository.GetSealedOrder(originalId).Returns(Task.FromResult<Shift?>(sealedOrder));

        var query = new GetResetDateRangeQuery(originalId);

        // Act
        var result = await _handler.Handle(query, CancellationToken.None);

        // Assert
        result.ShouldNotBeNull();
        result.EarliestResetDate.ShouldBe(fromDate);
        result.UntilDate.ShouldBeNull();
    }

    [Test]
    public async Task Handle_NoSealedOrderFound_ShouldThrowKeyNotFoundException()
    {
        // Arrange
        var originalId = Guid.NewGuid();

        _shiftRepository.GetSealedOrder(originalId).Returns(Task.FromResult<Shift?>(null));

        var query = new GetResetDateRangeQuery(originalId);

        // Act
        Func<Task> act = async () => await _handler.Handle(query, CancellationToken.None);

        // Assert
        (await Should.ThrowAsync<KeyNotFoundException>(act)).Message.ShouldBe($"SealedOrder with OriginalId {originalId} not found");
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

        _shiftRepository.GetSealedOrder(originalId).Returns(Task.FromResult<Shift?>(sealedOrder));

        var query = new GetResetDateRangeQuery(originalId);

        // Act
        var result = await _handler.Handle(query, CancellationToken.None);

        // Assert
        result.EarliestResetDate.ShouldBe(fromDate);
    }
}
