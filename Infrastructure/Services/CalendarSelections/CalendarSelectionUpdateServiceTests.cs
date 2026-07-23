// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests that CalendarSelectionUpdateService updates OfficialOverride on an already-existing
/// (Country, State) pair instead of only adding/removing pairs, so a changed reminder-only flag on a
/// merged calendar is persisted rather than silently discarded.
/// </summary>

namespace Klacks.UnitTest.Infrastructure.Services.CalendarSelections;

using Klacks.Api.Domain.Models.CalendarSelections;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.CalendarSelections;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

[TestFixture]
public class CalendarSelectionUpdateServiceTests
{
    private const string Country = "US";
    private const string State = "US";

    private DataBaseContext _context = null!;
    private CalendarSelectionUpdateService _sut = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _sut = new CalendarSelectionUpdateService(_context, Substitute.For<ILogger<CalendarSelectionUpdateService>>());
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public async Task UpdateCalendarSelectionAsync_ChangedOverrideOnExistingPair_PersistsNewValue()
    {
        var selectionId = Guid.NewGuid();
        _context.CalendarSelection.Add(new CalendarSelection
        {
            Id = selectionId,
            Name = "Override update test",
            SelectedCalendars =
            {
                new SelectedCalendar
                {
                    Id = Guid.NewGuid(),
                    CalendarSelectionId = selectionId,
                    Country = Country,
                    State = State,
                    OfficialOverride = null
                }
            }
        });
        await _context.SaveChangesAsync();

        var existing = await _sut.GetWithSelectedCalendarsAsync(selectionId);
        var updatedModel = new CalendarSelection
        {
            Id = selectionId,
            Name = "Override update test",
            SelectedCalendars =
            {
                new SelectedCalendar
                {
                    CalendarSelectionId = selectionId,
                    Country = Country,
                    State = State,
                    OfficialOverride = false
                }
            }
        };

        await _sut.UpdateCalendarSelectionAsync(existing, updatedModel);

        var reloaded = await _context.SelectedCalendar
            .AsNoTracking()
            .SingleAsync(sc => sc.CalendarSelectionId == selectionId);
        reloaded.OfficialOverride.ShouldBe(false);
        reloaded.Country.ShouldBe(Country);
        reloaded.State.ShouldBe(State);
    }
}
