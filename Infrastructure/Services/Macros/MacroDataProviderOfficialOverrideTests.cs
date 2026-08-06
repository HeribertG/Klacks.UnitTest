// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests that MacroDataProvider honours the per-calendar SelectedCalendar.OfficialOverride: a merged
/// calendar flagged OfficialOverride=false downgrades only its own holidays to UnofficialHoliday (present
/// in the list, but excluded from macroData.Holiday), while sibling calendars in the same selection keep
/// their inherited official status. Also guards that the underlying CalendarRule.IsMandatory is never
/// mutated in-place (which would persist the override globally through the shared scoped DbContext).
/// </summary>

namespace Klacks.UnitTest.Infrastructure.Services.Macros;

using Klacks.Api.Infrastructure.Services.Schedules;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Domain.Common;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.CalendarSelections;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Settings;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Scripting;
using Klacks.Api.Infrastructure.Services.Macros;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class MacroDataProviderOfficialOverrideTests
{
    private const string OverrideCountry = "US";
    private const string OverrideState = "US";
    private const string SiblingCountry = "CH";
    private const string SiblingState = "BE";
    private const int TestYear = 2026;

    private static readonly DateOnly OverrideHolidayDate = new(TestYear, 7, 4);
    private static readonly DateOnly SiblingHolidayDate = new(TestYear, 8, 1);

    private DataBaseContext _context = null!;
    private IHolidayCalculatorCache _holidayCache = null!;
    private IClientContractDataProvider _contractDataProvider = null!;
    private IWorkChangeEffectiveTimeService _effectiveTimeService = null!;
    private IWeekConfiguration _weekConfiguration = null!;
    private MacroDataProvider _sut = null!;
    private Guid _selectionId;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var httpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _context = new DataBaseContext(options, httpContextAccessor);

        _holidayCache = new HolidayCalculatorCache();
        _contractDataProvider = Substitute.For<IClientContractDataProvider>();
        _effectiveTimeService = Substitute.For<IWorkChangeEffectiveTimeService>();
        _weekConfiguration = Substitute.For<IWeekConfiguration>();
        _weekConfiguration.GetWeekendDaysAsync().Returns(new HashSet<DayOfWeek> { DayOfWeek.Saturday, DayOfWeek.Sunday });

        _sut = new MacroDataProvider(_context, new ClientHolidayCalendarResolver(_context, _holidayCache), _contractDataProvider, _effectiveTimeService, _weekConfiguration);
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public async Task GetMacroDataAsync_OverrideFalse_DowngradesOnlyOverriddenCalendarAndExcludesFromMacroHoliday()
    {
        await SeedSelectionAsync(overrideForUs: false);
        var work = CreateWork(OverrideHolidayDate);

        var macroData = await _sut.GetMacroDataAsync(work);

        macroData.Holiday.ShouldBeFalse();

        var calculator = await GetCachedCalculatorAsync();
        calculator.IsHoliday(OverrideHolidayDate).ShouldBe(HolidayStatus.UnofficialHoliday);
        calculator.IsHoliday(SiblingHolidayDate).ShouldBe(HolidayStatus.OfficialHoliday);

        _context.CalendarRule
            .Single(r => r.Country == OverrideCountry && r.State == OverrideState)
            .IsMandatory.ShouldBeTrue();
    }

    [Test]
    public async Task GetMacroDataAsync_OverrideNull_KeepsHolidayOfficialAndSetsMacroHoliday()
    {
        await SeedSelectionAsync(overrideForUs: null);
        var work = CreateWork(OverrideHolidayDate);

        var macroData = await _sut.GetMacroDataAsync(work);

        macroData.Holiday.ShouldBeTrue();

        var calculator = await GetCachedCalculatorAsync();
        calculator.IsHoliday(OverrideHolidayDate).ShouldBe(HolidayStatus.OfficialHoliday);
    }

    private async Task<IHolidaysListCalculator> GetCachedCalculatorAsync()
    {
        return await _holidayCache.GetOrCreateAsync(
            _selectionId,
            TestYear,
            () => Task.FromException<IHolidaysListCalculator>(
                new InvalidOperationException("Calculator must already be cached by the provider.")));
    }

    private async Task SeedSelectionAsync(bool? overrideForUs)
    {
        _selectionId = Guid.NewGuid();

        var selection = new CalendarSelection
        {
            Id = _selectionId,
            Name = "Override test selection",
            SelectedCalendars =
            {
                new SelectedCalendar
                {
                    Id = Guid.NewGuid(),
                    CalendarSelectionId = _selectionId,
                    Country = OverrideCountry,
                    State = OverrideState,
                    OfficialOverride = overrideForUs
                },
                new SelectedCalendar
                {
                    Id = Guid.NewGuid(),
                    CalendarSelectionId = _selectionId,
                    Country = SiblingCountry,
                    State = SiblingState,
                    OfficialOverride = null
                }
            }
        };

        _context.CalendarSelection.Add(selection);
        _context.CalendarRule.AddRange(
            new CalendarRule
            {
                Id = Guid.NewGuid(),
                Country = OverrideCountry,
                State = OverrideState,
                Name = new MultiLanguage { En = "Independence Day" },
                Rule = "07.04",
                IsMandatory = true
            },
            new CalendarRule
            {
                Id = Guid.NewGuid(),
                Country = SiblingCountry,
                State = SiblingState,
                Name = new MultiLanguage { En = "Swiss National Day" },
                Rule = "08.01",
                IsMandatory = true
            });

        await _context.SaveChangesAsync();

        _contractDataProvider
            .GetEffectiveContractDataAsync(Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<int?>())
            .Returns(new EffectiveContractData { CalendarSelectionId = _selectionId });
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
