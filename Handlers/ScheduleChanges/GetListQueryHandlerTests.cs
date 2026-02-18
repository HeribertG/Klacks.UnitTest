using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Handlers.ScheduleChanges;
using Klacks.Api.Application.Interfaces;
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
        var startDate = new DateOnly(2026, 1, 1);
        var endDate = new DateOnly(2026, 12, 31);
        var query = new GetListQuery { StartDate = startDate, EndDate = endDate };

        _mockTracker.GetChangesAsync(startDate, endDate)
            .Returns(Task.FromResult(new List<ScheduleChangeResource>()));

        await _handler.Handle(query, CancellationToken.None);

        await _mockTracker.Received(1).GetChangesAsync(startDate, endDate);
    }

    [Test]
    public async Task Handle_ShouldReturnResultsFromTracker()
    {
        var startDate = new DateOnly(2026, 2, 1);
        var endDate = new DateOnly(2026, 2, 28);
        var query = new GetListQuery { StartDate = startDate, EndDate = endDate };

        var expectedResults = new List<ScheduleChangeResource>
        {
            new() { ClientId = Guid.NewGuid(), ChangeDate = new DateOnly(2026, 2, 10) },
            new() { ClientId = Guid.NewGuid(), ChangeDate = new DateOnly(2026, 2, 15) }
        };

        _mockTracker.GetChangesAsync(startDate, endDate)
            .Returns(Task.FromResult(expectedResults));

        var result = await _handler.Handle(query, CancellationToken.None);

        result.Should().HaveCount(2);
        result.Should().BeEquivalentTo(expectedResults);
    }

    [Test]
    public async Task Handle_NoChanges_ShouldReturnEmptyList()
    {
        var query = new GetListQuery
        {
            StartDate = new DateOnly(2026, 3, 1),
            EndDate = new DateOnly(2026, 3, 31)
        };

        _mockTracker.GetChangesAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>())
            .Returns(Task.FromResult(new List<ScheduleChangeResource>()));

        var result = await _handler.Handle(query, CancellationToken.None);

        result.Should().BeEmpty();
    }
}
