// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for InboundShiftContextReader against the EF InMemory provider: returns the client's real
/// planned shifts in the window ordered by date and start, with the shift name as stored, and excludes
/// scenario rows (AnalyseToken set), soft-deleted rows, other clients and dates outside the window.
/// Every work row gets an existing shift, as the work -> shift foreign key guarantees in PostgreSQL
/// (the required Shift navigation joins inner under the shift query filter).
/// </summary>

using Klacks.Api.Infrastructure.Repositories.Inbound;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Klacks.UnitTest.Infrastructure.Repositories.Inbound;

[TestFixture]
public class InboundShiftContextReaderTests
{
    private static readonly Guid ClientId = Guid.NewGuid();
    private static readonly Guid LateShiftId = Guid.NewGuid();
    private static readonly DateOnly Day = new(2026, 9, 23);

    private DataBaseContext _context = null!;
    private InboundShiftContextReader _reader = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _reader = new InboundShiftContextReader(_context);
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    private void AddWork(Guid clientId, DateOnly date, int startHour, int endHour, Guid? shiftId = null, Guid? analyseToken = null, bool isDeleted = false)
    {
        if (shiftId is null)
        {
            shiftId = Guid.NewGuid();
            _context.Shift.Add(new Shift { Id = shiftId.Value, Name = string.Empty, Abbreviation = "X" });
        }

        _context.Work.Add(new Work
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            ShiftId = shiftId.Value,
            CurrentDate = date,
            StartTime = new TimeOnly(startHour, 0),
            EndTime = new TimeOnly(endHour, 0),
            AnalyseToken = analyseToken,
            IsDeleted = isDeleted
        });
    }

    [Test]
    public async Task ReturnsOnlyRealShiftsOfTheClientInTheWindow_OrderedWithName()
    {
        _context.Shift.Add(new Shift { Id = LateShiftId, Name = "Spätdienst Chirurgie", Abbreviation = "SD" });
        AddWork(ClientId, Day, 14, 22, LateShiftId);
        AddWork(ClientId, Day, 6, 14);
        AddWork(ClientId, Day, 8, 16, analyseToken: Guid.NewGuid());
        AddWork(ClientId, Day, 9, 17, isDeleted: true);
        AddWork(Guid.NewGuid(), Day, 6, 14);
        AddWork(ClientId, Day.AddDays(5), 6, 14);
        await _context.SaveChangesAsync();

        var shifts = await _reader.GetShiftsAsync(ClientId, Day, Day.AddDays(1), 10);

        shifts.Count.ShouldBe(2);
        shifts[0].StartTime.ShouldBe(new TimeOnly(6, 0));
        shifts[0].ShiftName.ShouldBe(string.Empty);
        shifts[1].StartTime.ShouldBe(new TimeOnly(14, 0));
        shifts[1].ShiftName.ShouldBe("Spätdienst Chirurgie");
    }

    [Test]
    public async Task RespectsTheMaximumCount()
    {
        AddWork(ClientId, Day, 6, 14);
        AddWork(ClientId, Day, 14, 22);
        AddWork(ClientId, Day.AddDays(1), 6, 14);
        await _context.SaveChangesAsync();

        var shifts = await _reader.GetShiftsAsync(ClientId, Day, Day.AddDays(1), 2);

        shifts.Count.ShouldBe(2);
    }
}
