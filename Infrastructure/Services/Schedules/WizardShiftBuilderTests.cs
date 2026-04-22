// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using FluentAssertions;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.Schedules;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NUnit.Framework;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules;

[TestFixture]
public class WizardShiftBuilderTests
{
    private DataBaseContext _context = null!;
    private WizardShiftBuilder _sut = null!;

    [SetUp]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        var httpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _context = new DataBaseContext(options, httpContextAccessor);
        _sut = new WizardShiftBuilder(_context);
    }

    [TearDown]
    public void TearDown()
    {
        _context.Dispose();
    }

    [Test]
    public async Task BuildAsync_Quantity1_ProducesOneSlotPerActiveDay()
    {
        var monday = new DateOnly(2026, 4, 20);

        _context.Shift.Add(new Shift
        {
            Id = Guid.NewGuid(),
            Name = "Frühdienst",
            Abbreviation = "FD",
            FromDate = monday,
            UntilDate = monday,
            StartShift = new TimeOnly(6, 0),
            EndShift = new TimeOnly(14, 0),
            WorkTime = 8m,
            Quantity = 1,
            IsMonday = true,
        });
        await _context.SaveChangesAsync();

        var result = await _sut.BuildAsync(null, monday, monday, CancellationToken.None);

        result.Should().HaveCount(1);
        result[0].RequiredAssignments.Should().Be(1);
    }

    [Test]
    public async Task BuildAsync_Quantity3_ProducesThreeSlotsPerActiveDay()
    {
        var monday = new DateOnly(2026, 4, 20);
        var shiftId = Guid.NewGuid();

        _context.Shift.Add(new Shift
        {
            Id = shiftId,
            Name = "Frühdienst",
            Abbreviation = "FD",
            FromDate = monday,
            UntilDate = monday,
            StartShift = new TimeOnly(6, 0),
            EndShift = new TimeOnly(14, 0),
            WorkTime = 8m,
            Quantity = 3,
            IsMonday = true,
        });
        await _context.SaveChangesAsync();

        var result = await _sut.BuildAsync(null, monday, monday, CancellationToken.None);

        result.Should().HaveCount(3);
        result.Should().OnlyContain(s => s.Id == shiftId.ToString());
        result.Should().OnlyContain(s => s.Date == "2026-04-20");
        result.Should().OnlyContain(s => s.RequiredAssignments == 1);
    }

    [Test]
    public async Task BuildAsync_Quantity3_TwoActiveDays_ProducesSixSlots()
    {
        var monday = new DateOnly(2026, 4, 20);
        var tuesday = new DateOnly(2026, 4, 21);

        _context.Shift.Add(new Shift
        {
            Id = Guid.NewGuid(),
            Name = "Frühdienst",
            Abbreviation = "FD",
            FromDate = monday,
            UntilDate = tuesday,
            StartShift = new TimeOnly(6, 0),
            EndShift = new TimeOnly(14, 0),
            WorkTime = 8m,
            Quantity = 3,
            IsMonday = true,
            IsTuesday = true,
        });
        await _context.SaveChangesAsync();

        var result = await _sut.BuildAsync(null, monday, tuesday, CancellationToken.None);

        result.Should().HaveCount(6);
        result.Where(s => s.Date == "2026-04-20").Should().HaveCount(3);
        result.Where(s => s.Date == "2026-04-21").Should().HaveCount(3);
    }
}
