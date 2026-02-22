using Klacks.Api.Application.Interfaces;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Klacks.UnitTest.Services.Schedules;

[TestFixture]
public class ScheduleChangeTrackerTests
{
    private DataBaseContext _context;
    private ScheduleChangeTracker _tracker;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var mockHttpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _context = new DataBaseContext(options, mockHttpContextAccessor);
        var mockNotificationService = Substitute.For<IWorkNotificationService>();
        _tracker = new ScheduleChangeTracker(_context, mockNotificationService, mockHttpContextAccessor);
    }

    [TearDown]
    public void TearDown()
    {
        _context?.Dispose();
    }

    [Test]
    public async Task TrackChangeAsync_NewEntry_ShouldCreateRecord()
    {
        var clientId = Guid.NewGuid();
        var changeDate = new DateOnly(2026, 2, 19);

        await _tracker.TrackChangeAsync(clientId, changeDate);

        var entries = await _context.ScheduleChange.ToListAsync();
        entries.Should().HaveCount(1);
        entries[0].ClientId.Should().Be(clientId);
        entries[0].ChangeDate.Should().Be(changeDate);
        entries[0].CreateTime.Should().NotBeNull();
    }

    [Test]
    public async Task TrackChangeAsync_ExistingEntry_ShouldUpdateTimestamp()
    {
        var clientId = Guid.NewGuid();
        var changeDate = new DateOnly(2026, 2, 19);

        await _tracker.TrackChangeAsync(clientId, changeDate);
        var firstEntry = await _context.ScheduleChange.FirstAsync();
        var firstUpdateTime = firstEntry.UpdateTime;

        await Task.Delay(10);
        await _tracker.TrackChangeAsync(clientId, changeDate);

        var entries = await _context.ScheduleChange.ToListAsync();
        entries.Should().HaveCount(1);
        entries[0].UpdateTime.Should().NotBeNull();
        entries[0].UpdateTime.Should().NotBe(firstUpdateTime);
    }

    [Test]
    public async Task TrackChangeAsync_DifferentClients_ShouldCreateSeparateRecords()
    {
        var clientId1 = Guid.NewGuid();
        var clientId2 = Guid.NewGuid();
        var changeDate = new DateOnly(2026, 2, 19);

        await _tracker.TrackChangeAsync(clientId1, changeDate);
        await _tracker.TrackChangeAsync(clientId2, changeDate);

        var entries = await _context.ScheduleChange.ToListAsync();
        entries.Should().HaveCount(2);
        entries.Select(e => e.ClientId).Should().Contain(clientId1);
        entries.Select(e => e.ClientId).Should().Contain(clientId2);
    }

    [Test]
    public async Task TrackChangeAsync_DifferentDates_ShouldCreateSeparateRecords()
    {
        var clientId = Guid.NewGuid();
        var date1 = new DateOnly(2026, 2, 18);
        var date2 = new DateOnly(2026, 2, 19);

        await _tracker.TrackChangeAsync(clientId, date1);
        await _tracker.TrackChangeAsync(clientId, date2);

        var entries = await _context.ScheduleChange.ToListAsync();
        entries.Should().HaveCount(2);
        entries.Select(e => e.ChangeDate).Should().Contain(date1);
        entries.Select(e => e.ChangeDate).Should().Contain(date2);
    }

    [Test]
    public async Task GetChangesAsync_WithDateRange_ShouldReturnFilteredResults()
    {
        var clientId = Guid.NewGuid();
        await _tracker.TrackChangeAsync(clientId, new DateOnly(2026, 1, 15));
        await _tracker.TrackChangeAsync(clientId, new DateOnly(2026, 2, 10));
        await _tracker.TrackChangeAsync(clientId, new DateOnly(2026, 3, 20));

        var results = await _tracker.GetChangesAsync(
            new DateOnly(2026, 2, 1),
            new DateOnly(2026, 2, 28));

        results.Should().HaveCount(1);
        results[0].ChangeDate.Should().Be(new DateOnly(2026, 2, 10));
    }

    [Test]
    public async Task GetChangesAsync_InclusiveBoundaries_ShouldIncludeStartAndEndDates()
    {
        var clientId = Guid.NewGuid();
        var startDate = new DateOnly(2026, 2, 1);
        var endDate = new DateOnly(2026, 2, 28);

        await _tracker.TrackChangeAsync(clientId, startDate);
        await _tracker.TrackChangeAsync(clientId, endDate);

        var results = await _tracker.GetChangesAsync(startDate, endDate);

        results.Should().HaveCount(2);
    }

    [Test]
    public async Task GetChangesAsync_NoMatches_ShouldReturnEmptyList()
    {
        var clientId = Guid.NewGuid();
        await _tracker.TrackChangeAsync(clientId, new DateOnly(2026, 1, 15));

        var results = await _tracker.GetChangesAsync(
            new DateOnly(2026, 3, 1),
            new DateOnly(2026, 3, 31));

        results.Should().BeEmpty();
    }

    [Test]
    public async Task GetChangesAsync_MultipleClients_ShouldReturnAll()
    {
        var clientId1 = Guid.NewGuid();
        var clientId2 = Guid.NewGuid();
        var date = new DateOnly(2026, 2, 15);

        await _tracker.TrackChangeAsync(clientId1, date);
        await _tracker.TrackChangeAsync(clientId2, date);

        var results = await _tracker.GetChangesAsync(
            new DateOnly(2026, 2, 1),
            new DateOnly(2026, 2, 28));

        results.Should().HaveCount(2);
        results.Select(r => r.ClientId).Should().Contain(clientId1);
        results.Select(r => r.ClientId).Should().Contain(clientId2);
    }

    [Test]
    public async Task GetChangesAsync_ShouldReturnCorrectResourceProperties()
    {
        var clientId = Guid.NewGuid();
        var changeDate = new DateOnly(2026, 2, 15);

        await _tracker.TrackChangeAsync(clientId, changeDate);

        var results = await _tracker.GetChangesAsync(
            new DateOnly(2026, 2, 1),
            new DateOnly(2026, 2, 28));

        results.Should().HaveCount(1);
        results[0].ClientId.Should().Be(clientId);
        results[0].ChangeDate.Should().Be(changeDate);
    }
}
