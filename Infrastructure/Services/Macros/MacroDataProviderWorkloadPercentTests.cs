// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests that MacroDataProvider copies EffectiveContractData.WorkloadPercent into the macro input
/// for work and break entries, so the absence macros can scale by the contract workload instead of
/// deriving a month-dependent ratio from GuaranteedHours / FullTime.
/// </summary>

namespace Klacks.UnitTest.Infrastructure.Services.Macros;

using Klacks.Api.Infrastructure.Services.Schedules;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.Macros;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class MacroDataProviderWorkloadPercentTests
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
        _effectiveTimeService = Substitute.For<IWorkChangeEffectiveTimeService>();
        _weekConfiguration = Substitute.For<IWeekConfiguration>();
        _weekConfiguration.GetWeekendDaysAsync().Returns(new HashSet<DayOfWeek> { DayOfWeek.Saturday, DayOfWeek.Sunday });

        _sut = new MacroDataProvider(
            _context,
            new ClientHolidayCalendarResolver(_context, _holidayCache),
            _contractDataProvider,
            _effectiveTimeService,
            _weekConfiguration);
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public async Task GetMacroDataForBreakAsync_InheritingPartTimeContract_CopiesWorkloadPercent()
    {
        _contractDataProvider.GetEffectiveContractDataAsync(Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<int?>())
            .Returns(new EffectiveContractData { WorkloadPercent = 60m, DefaultWorkingHours = 8.5m });
        var breakEntry = CreateBreak(new DateOnly(2026, 2, 4));

        var macroData = await _sut.GetMacroDataForBreakAsync(breakEntry);

        macroData.WorkloadPercent.ShouldBe(60m);
        macroData.Hour.ShouldBe(8.5m);
    }

    [Test]
    public async Task GetMacroDataForBreakAsync_DefaultContractData_IsFullWorkload()
    {
        _contractDataProvider.GetEffectiveContractDataAsync(Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<int?>())
            .Returns(new EffectiveContractData());
        var breakEntry = CreateBreak(new DateOnly(2026, 2, 4));

        var macroData = await _sut.GetMacroDataForBreakAsync(breakEntry);

        macroData.WorkloadPercent.ShouldBe(100m);
    }

    [Test]
    public async Task GetMacroDataForBreakAsync_FullDayBreakAsBookedBySchedule_YieldsFullDayMarkerTimes()
    {
        // The schedule books full-day absences as 00:00:00-23:59:00 (calculateBreakTimes fallback in
        // schedule-entry-actions.service.ts). This pins the exact FromHour/UntilHour strings the Paid
        // Absence macro's full-day detection relies on.
        _contractDataProvider.GetEffectiveContractDataAsync(Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<int?>())
            .Returns(new EffectiveContractData());
        var breakEntry = CreateBreak(new DateOnly(2026, 2, 4));
        breakEntry.StartTime = new TimeOnly(0, 0, 0);
        breakEntry.EndTime = new TimeOnly(23, 59, 0);

        var macroData = await _sut.GetMacroDataForBreakAsync(breakEntry);

        macroData.FromHour.ShouldBe("00:00");
        macroData.UntilHour.ShouldBe("23:59");
    }

    [Test]
    public async Task FullDayBreak_ThroughRealProviderIntoPaidAbsenceMacro_CreditsNothing()
    {
        // End-to-end proof for the owner ruling 2026-08-19: a full-day booked paid absence
        // (00:00:00-23:59:00 as the schedule stores it) must credit 0. The macro receives the REAL
        // provider output, not hand-picked times.
        _contractDataProvider.GetEffectiveContractDataAsync(Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<int?>())
            .Returns(new EffectiveContractData { DefaultWorkingHours = 8.5m });
        var breakEntry = CreateBreak(new DateOnly(2026, 2, 4));
        breakEntry.StartTime = new TimeOnly(0, 0, 0);
        breakEntry.EndTime = new TimeOnly(23, 59, 0);

        var macroData = await _sut.GetMacroDataForBreakAsync(breakEntry);

        var compiled = Klacks.Api.Infrastructure.Scripting.CompiledScript.Compile(
            Klacks.UnitTest.Scripting.AbsenceMacroPercentTest.PaidAbsenceMacro);
        compiled.HasError.ShouldBeFalse(compiled.Error?.Description);
        compiled.SetExternalValue("hour", macroData.Hour);
        compiled.SetExternalValue("fromhour", macroData.FromHour);
        compiled.SetExternalValue("untilhour", macroData.UntilHour);
        compiled.SetExternalValue("weekday", macroData.Weekday);
        compiled.SetExternalValue("holiday", macroData.Holiday ? 1 : 0);
        compiled.SetExternalValue("holidaynextday", macroData.HolidayNextDay ? 1 : 0);
        compiled.SetExternalValue("nightrate", macroData.NightRate);
        compiled.SetExternalValue("holidayrate", macroData.HolidayRate);
        compiled.SetExternalValue("we1rate", macroData.WE1Rate);
        compiled.SetExternalValue("we2rate", macroData.WE2Rate);
        compiled.SetExternalValue("guaranteedhours", macroData.GuaranteedHours);
        compiled.SetExternalValue("fulltime", macroData.FullTime);
        compiled.SetExternalValue("percent", macroData.WorkloadPercent);

        var executionResult = new Klacks.Api.Infrastructure.Scripting.ScriptExecutionContext(compiled).Execute();

        executionResult.Success.ShouldBeTrue(executionResult.Error?.Description);
        var output = executionResult.Messages.First(m => m.Type == 1);
        decimal.Parse(output.Message, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture)
            .ShouldBe(0m);
    }

    [Test]
    public async Task GetMacroDataAsync_Work_CopiesWorkloadPercent()
    {
        _contractDataProvider.GetEffectiveContractDataAsync(Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<int?>())
            .Returns(new EffectiveContractData { WorkloadPercent = 40m });
        var work = CreateWork(new DateOnly(2026, 2, 4));

        var macroData = await _sut.GetMacroDataAsync(work);

        macroData.WorkloadPercent.ShouldBe(40m);
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

    private static Break CreateBreak(DateOnly date) => new()
    {
        Id = Guid.NewGuid(),
        ClientId = Guid.NewGuid(),
        AbsenceId = Guid.NewGuid(),
        CurrentDate = date,
        StartTime = new TimeOnly(8, 0),
        EndTime = new TimeOnly(16, 0),
    };
}
