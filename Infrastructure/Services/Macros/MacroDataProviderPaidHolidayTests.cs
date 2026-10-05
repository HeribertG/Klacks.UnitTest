// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests the owner rule for the holiday time surcharge: macroData.Holiday / HolidayNextDay are set only when the
/// holiday in the employee's contract calendar is OFFICIAL (effective IsMandatory including OfficialOverride) AND
/// its calendar rule is PAID. Unofficial, reminder-only and unpaid holidays never set the flags. This applies to the
/// work and work-change paths; the absence (break) path keeps the official-only flag, because the seeded absence
/// macros use it to count an absence on a holiday as 0 hours - a different semantic the owner has not decided yet.
/// </summary>

namespace Klacks.UnitTest.Infrastructure.Services.Macros;

using Klacks.Api.Infrastructure.Scripting;
using Klacks.Api.Infrastructure.Services.Macros;
using Klacks.Api.Infrastructure.Services.Schedules;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

[TestFixture]
public class MacroDataProviderPaidHolidayTests
{
    private const string OfficialCountry = "CH";
    private const string OfficialState = "BE";
    private const string ReminderCountry = "US";
    private const string ReminderState = "US";
    private const string ChristmasRule = "12.25";
    private const string NewYearRule = "01.01";
    private const int TestYear = 2026;

    private static readonly DateOnly Christmas = new(TestYear, 12, 25);
    private static readonly DateOnly ChristmasEve = new(TestYear, 12, 24);
    private static readonly DateOnly NewYearsEve = new(TestYear, 12, 31);
    private static readonly TimeOnly WorkStart = new(8, 0);
    private static readonly TimeOnly WorkEnd = new(16, 0);

    private DataBaseContext _context = null!;
    private IClientContractDataProvider _contractDataProvider = null!;
    private IWorkChangeEffectiveTimeService _effectiveTimeService = null!;
    private MacroDataProvider _sut = null!;
    private Guid _selectionId;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());

        _contractDataProvider = Substitute.For<IClientContractDataProvider>();
        _effectiveTimeService = Substitute.For<IWorkChangeEffectiveTimeService>();
        _effectiveTimeService.GetEffectiveTimesAsync(Arg.Any<WorkChange>(), Arg.Any<Work>(), Arg.Any<Shift?>())
            .Returns((WorkStart, WorkEnd));
        var weekConfiguration = Substitute.For<IWeekConfiguration>();
        weekConfiguration.GetWeekendDaysAsync().Returns(new HashSet<DayOfWeek> { DayOfWeek.Saturday, DayOfWeek.Sunday });

        _sut = new MacroDataProvider(
            _context,
            new ClientHolidayCalendarResolver(_context, new HolidayCalculatorCache()),
            _contractDataProvider,
            _effectiveTimeService,
            weekConfiguration);
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [TestCase(true, true, true)]
    [TestCase(true, false, false)]
    [TestCase(false, true, false)]
    [TestCase(false, false, false)]
    public async Task Holiday_RequiresOfficialAndPaid(bool isMandatory, bool isPaid, bool expected)
    {
        await SeedAsync(reminderOverride: null, CreateRule(OfficialCountry, OfficialState, ChristmasRule, isMandatory, isPaid));

        var macroData = await _sut.GetMacroDataAsync(CreateWork(Christmas));

        macroData.Holiday.ShouldBe(expected);
    }

    [Test]
    public async Task Holiday_ReminderOnlyCalendarWithPaidRule_NoSurcharge()
    {
        await SeedAsync(reminderOverride: false, CreateRule(ReminderCountry, ReminderState, ChristmasRule, isMandatory: true, isPaid: true));

        var macroData = await _sut.GetMacroDataAsync(CreateWork(Christmas));

        macroData.Holiday.ShouldBeFalse();
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task Holiday_OfficialPaidAndReminderOnSameDate_Surcharge(bool seedReminderFirst)
    {
        var official = CreateRule(OfficialCountry, OfficialState, ChristmasRule, isMandatory: true, isPaid: true);
        var reminder = CreateRule(ReminderCountry, ReminderState, ChristmasRule, isMandatory: true, isPaid: true);
        await SeedAsync(reminderOverride: false, seedReminderFirst ? [reminder, official] : [official, reminder]);

        var macroData = await _sut.GetMacroDataAsync(CreateWork(Christmas));

        macroData.Holiday.ShouldBeTrue();
    }

    [TestCase(true, true)]
    [TestCase(false, false)]
    public async Task HolidayNextDay_RequiresPaidOfficialHolidayTomorrow(bool isPaid, bool expected)
    {
        await SeedAsync(reminderOverride: null, CreateRule(OfficialCountry, OfficialState, ChristmasRule, isMandatory: true, isPaid));

        var macroData = await _sut.GetMacroDataAsync(CreateWork(ChristmasEve));

        macroData.Holiday.ShouldBeFalse();
        macroData.HolidayNextDay.ShouldBe(expected);
    }

    [Test]
    public async Task HolidayNextDay_ReminderOnlyPaidHolidayTomorrow_NoSurcharge()
    {
        await SeedAsync(reminderOverride: false, CreateRule(ReminderCountry, ReminderState, ChristmasRule, isMandatory: true, isPaid: true));

        var macroData = await _sut.GetMacroDataAsync(CreateWork(ChristmasEve));

        macroData.HolidayNextDay.ShouldBeFalse();
    }

    [TestCase(true, true)]
    [TestCase(false, false)]
    public async Task HolidayNextDay_AcrossYearEnd_UsesNextYearsPaidFlag(bool isPaid, bool expected)
    {
        await SeedAsync(reminderOverride: null, CreateRule(OfficialCountry, OfficialState, NewYearRule, isMandatory: true, isPaid));

        var macroData = await _sut.GetMacroDataAsync(CreateWork(NewYearsEve));

        macroData.HolidayNextDay.ShouldBe(expected);
    }

    [TestCase(true, true)]
    [TestCase(false, false)]
    public async Task WorkChange_RequiresPaidOfficialHoliday(bool isPaid, bool expected)
    {
        await SeedAsync(reminderOverride: null, CreateRule(OfficialCountry, OfficialState, ChristmasRule, isMandatory: true, isPaid));
        var work = CreateWork(Christmas);

        var macroData = await _sut.GetMacroDataForWorkChangeAsync(
            new WorkChange { Id = Guid.NewGuid(), WorkId = work.Id, ChangeTime = 1m }, work);

        macroData.Holiday.ShouldBe(expected);
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task Break_OfficialHoliday_KeepsHolidayFlagRegardlessOfIsPaid(bool isPaid)
    {
        await SeedAsync(reminderOverride: null, CreateRule(OfficialCountry, OfficialState, ChristmasRule, isMandatory: true, isPaid));

        var macroData = await _sut.GetMacroDataForBreakAsync(new Break
        {
            Id = Guid.NewGuid(),
            ClientId = Guid.NewGuid(),
            CurrentDate = Christmas,
            StartTime = WorkStart,
            EndTime = WorkEnd
        });

        macroData.Holiday.ShouldBeTrue();
    }

    private async Task SeedAsync(bool? reminderOverride, params CalendarRule[] rules)
    {
        _selectionId = Guid.NewGuid();
        _context.CalendarSelection.Add(new CalendarSelection
        {
            Id = _selectionId,
            Name = "Paid holiday test selection",
            SelectedCalendars =
            {
                new SelectedCalendar
                {
                    Id = Guid.NewGuid(),
                    CalendarSelectionId = _selectionId,
                    Country = OfficialCountry,
                    State = OfficialState,
                    OfficialOverride = null
                },
                new SelectedCalendar
                {
                    Id = Guid.NewGuid(),
                    CalendarSelectionId = _selectionId,
                    Country = ReminderCountry,
                    State = ReminderState,
                    OfficialOverride = reminderOverride
                }
            }
        });
        _context.CalendarRule.AddRange(rules);
        await _context.SaveChangesAsync();

        _contractDataProvider
            .GetEffectiveContractDataAsync(Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<int?>())
            .Returns(new EffectiveContractData { CalendarSelectionId = _selectionId });
    }

    private static CalendarRule CreateRule(string country, string state, string rule, bool isMandatory, bool isPaid) => new()
    {
        Id = Guid.NewGuid(),
        Country = country,
        State = state,
        Name = new MultiLanguage { En = $"{country} {rule}" },
        Rule = rule,
        SubRule = string.Empty,
        IsMandatory = isMandatory,
        IsPaid = isPaid
    };

    private static Work CreateWork(DateOnly date) => new()
    {
        Id = Guid.NewGuid(),
        ClientId = Guid.NewGuid(),
        ShiftId = Guid.NewGuid(),
        CurrentDate = date,
        StartTime = WorkStart,
        EndTime = WorkEnd,
        WorkTime = 8m
    };
}
