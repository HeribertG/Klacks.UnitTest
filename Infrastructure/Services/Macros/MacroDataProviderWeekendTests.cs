// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests that MacroDataProvider resolves WeekendDay1/WeekendDay2 to the ISO weekday numbers of the
/// configured weekend days (ordered by ISO weekday), instead of the literal Saturday(6)/Sunday(7)
/// the built-in surcharge macros historically assumed.
/// </summary>

namespace Klacks.UnitTest.Infrastructure.Services.Macros;

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.Macros;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class MacroDataProviderWeekendTests
{
    private DataBaseContext _context = null!;
    private IHolidayCalculatorCache _holidayCache = null!;
    private IClientContractDataProvider _contractDataProvider = null!;
    private IWorkChangeEffectiveTimeService _effectiveTimeService = null!;
    private IWeekConfiguration _weekConfiguration = null!;
    private MacroDataProvider _sut = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var httpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _context = new DataBaseContext(options, httpContextAccessor);

        _holidayCache = Substitute.For<IHolidayCalculatorCache>();
        _contractDataProvider = Substitute.For<IClientContractDataProvider>();
        _contractDataProvider.GetEffectiveContractDataAsync(Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<int?>())
            .Returns(new EffectiveContractData());
        _effectiveTimeService = Substitute.For<IWorkChangeEffectiveTimeService>();
        _weekConfiguration = Substitute.For<IWeekConfiguration>();

        _sut = new MacroDataProvider(_context, _holidayCache, _contractDataProvider, _effectiveTimeService, _weekConfiguration);
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public async Task GetMacroDataAsync_DefaultSaturdaySundayWeekend_ResolvesToIsoWeekdaySixAndSeven()
    {
        _weekConfiguration.GetWeekendDaysAsync().Returns(new HashSet<DayOfWeek> { DayOfWeek.Saturday, DayOfWeek.Sunday });
        var work = CreateWork(new DateOnly(2026, 7, 11)); // Saturday

        var macroData = await _sut.GetMacroDataAsync(work);

        macroData.WeekendDay1.ShouldBe(6);
        macroData.WeekendDay2.ShouldBe(7);
    }

    [Test]
    public async Task GetMacroDataAsync_GulfClusterFridaySaturdayWeekend_ResolvesFridayFirstThenSaturday()
    {
        _weekConfiguration.GetWeekendDaysAsync().Returns(new HashSet<DayOfWeek> { DayOfWeek.Friday, DayOfWeek.Saturday });
        var work = CreateWork(new DateOnly(2026, 7, 10)); // Friday

        var macroData = await _sut.GetMacroDataAsync(work);

        macroData.WeekendDay1.ShouldBe(5); // Friday, ISO weekday 5
        macroData.WeekendDay2.ShouldBe(6); // Saturday, ISO weekday 6
    }

    [Test]
    public async Task GetMacroDataAsync_SingleConfiguredWeekendDay_SecondSlotStaysUnused()
    {
        _weekConfiguration.GetWeekendDaysAsync().Returns(new HashSet<DayOfWeek> { DayOfWeek.Friday });
        var work = CreateWork(new DateOnly(2026, 7, 10));

        var macroData = await _sut.GetMacroDataAsync(work);

        macroData.WeekendDay1.ShouldBe(5);
        macroData.WeekendDay2.ShouldBe(0);
    }

    [Test]
    public async Task GetMacroDataAsync_EffectiveContractDataCarriesCountrySpecificWindow_CopiesNightStartAndNightEnd()
    {
        _weekConfiguration.GetWeekendDaysAsync().Returns(new HashSet<DayOfWeek> { DayOfWeek.Saturday, DayOfWeek.Sunday });
        _contractDataProvider.GetEffectiveContractDataAsync(Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<int?>())
            .Returns(new EffectiveContractData { NightStart = "22:00", NightEnd = "04:00" });
        var work = CreateWork(new DateOnly(2026, 7, 11));

        var macroData = await _sut.GetMacroDataAsync(work);

        macroData.NightStart.ShouldBe("22:00");
        macroData.NightEnd.ShouldBe("04:00");
    }

    private static Work CreateWork(DateOnly date) => new()
    {
        Id = Guid.NewGuid(),
        ClientId = Guid.NewGuid(),
        ShiftId = Guid.NewGuid(),
        CurrentDate = date,
        StartTime = new TimeOnly(8, 0),
        EndTime = new TimeOnly(16, 0),
        WorkTime = 8m
    };
}
