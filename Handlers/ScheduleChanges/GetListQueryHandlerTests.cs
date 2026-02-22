using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Handlers.ScheduleChanges;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Schedules;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Handlers.ScheduleChanges;

[TestFixture]
public class GetListQueryHandlerTests
{
    private IScheduleChangeTracker _mockTracker;
    private GetListQueryHandler _handler;

    [SetUp]
    public void SetUp()
    {
        _mockTracker = Substitute.For<IScheduleChangeTracker>();
        var mockLogger = Substitute.For<ILogger<GetListQueryHandler>>();
        _handler = new GetListQueryHandler(_mockTracker, mockLogger);
    }

    [Test]
    public async Task Handle_ShouldCallGetChangesAsyncWithCorrectDates()
    {
        // Arrange
        var startDate = new DateOnly(2026, 1, 1);
        var endDate = new DateOnly(2026, 12, 31);
        var query = new GetListQuery { StartDate = startDate, EndDate = endDate };

        _mockTracker.GetChangesAsync(startDate, endDate)
            .Returns(Task.FromResult(new List<ScheduleChange>()));

        // Act
        await _handler.Handle(query, CancellationToken.None);

        // Assert
        await _mockTracker.Received(1).GetChangesAsync(startDate, endDate);
    }

    [Test]
    public async Task Handle_ShouldReturnResultsFromTracker()
    {
        // Arrange
        var startDate = new DateOnly(2026, 2, 1);
        var endDate = new DateOnly(2026, 2, 28);
        var query = new GetListQuery { StartDate = startDate, EndDate = endDate };

        var clientId1 = Guid.NewGuid();
        var clientId2 = Guid.NewGuid();
        var domainChanges = new List<ScheduleChange>
        {
            new() { ClientId = clientId1, ChangeDate = new DateOnly(2026, 2, 10) },
            new() { ClientId = clientId2, ChangeDate = new DateOnly(2026, 2, 15) }
        };

        _mockTracker.GetChangesAsync(startDate, endDate)
            .Returns(Task.FromResult(domainChanges));

        // Act
        var result = await _handler.Handle(query, CancellationToken.None);

        // Assert
        result.Should().HaveCount(2);
        result[0].ClientId.Should().Be(clientId1);
        result[0].ChangeDate.Should().Be(new DateOnly(2026, 2, 10));
        result[1].ClientId.Should().Be(clientId2);
        result[1].ChangeDate.Should().Be(new DateOnly(2026, 2, 15));
    }

    [Test]
    public async Task Handle_NoChanges_ShouldReturnEmptyList()
    {
        // Arrange
        var query = new GetListQuery
        {
            StartDate = new DateOnly(2026, 3, 1),
            EndDate = new DateOnly(2026, 3, 31)
        };

        _mockTracker.GetChangesAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>())
            .Returns(Task.FromResult(new List<ScheduleChange>()));

        // Act
        var result = await _handler.Handle(query, CancellationToken.None);

        // Assert
        result.Should().BeEmpty();
    }
}
