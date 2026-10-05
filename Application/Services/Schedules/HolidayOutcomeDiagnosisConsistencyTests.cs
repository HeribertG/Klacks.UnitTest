// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the holiday diagnosis Klacksy gives to what production actually does, on one fixture and three real paths:
/// the surcharge verdict must equal macroData.Holiday from MacroDataProvider, the warning verdict must equal the
/// output of HolidayWorkEvaluator, and the explanation (reason code) must never be "Unexplained" for the covered
/// constellations. Calendar resolution runs through the real ClientHolidayCalendarResolver on an in-memory database,
/// so a "reminder only" entry, an unpaid rule, an exemption and the company country/region fallback are judged by the
/// production code, not by a copy. When the explanation drifts from production, this test fails.
/// </summary>

namespace Klacks.UnitTest.Application.Services.Schedules;

using Klacks.Api.Application.Constants;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.Common;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Interfaces.Scheduling;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.CalendarSelections;
using Klacks.Api.Domain.Models.Scheduling;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Settings;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Scripting;
using Klacks.Api.Infrastructure.Services.Macros;
using Klacks.Api.Infrastructure.Services.Schedules;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using SettingsEntity = Klacks.Api.Domain.Models.Settings.Settings;

[TestFixture]
public class HolidayOutcomeDiagnosisConsistencyTests
{
    private const string ChristmasRule = "12.25";
    private const string Country = "CH";
    private const string State = "BE";
    private const string ReminderCountry = "US";
    private static readonly DateOnly Christmas = new(2026, 12, 25);
    private static readonly DateOnly Ordinary = new(2026, 12, 22);
    private static readonly Guid ContractRuleId = Guid.NewGuid();

    private DataBaseContext _context = null!;
    private Dictionary<string, string> _settings = null!;
    private List<HolidayWorkExemptionRule> _exemptions = null!;
    private EffectiveContractData _contract = null!;
    private Guid _clientId;
    private MacroDataProvider _macroDataProvider = null!;
    private HolidayWorkEvaluator _evaluator = null!;
    private HolidayOutcomeDiagnosisService _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _clientId = Guid.NewGuid();
        _settings = new Dictionary<string, string>();
        _exemptions = new List<HolidayWorkExemptionRule>();
        _contract = new EffectiveContractData { HasActiveContract = true, HolidayRate = 1m, SchedulingRuleId = ContractRuleId };

        var options = new DbContextOptionsBuilder<DataBaseContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());

        var contractData = Substitute.For<IClientContractDataProvider>();
        contractData.GetEffectiveContractDataAsync(Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<int?>())
            .Returns(_ => _contract);
        contractData.GetEffectiveContractDataForClientsRangeAsync(Arg.Any<List<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<int?>())
            .Returns(ci =>
            {
                var map = new Dictionary<DateOnly, Dictionary<Guid, EffectiveContractData>>();
                for (var day = ci.ArgAt<DateOnly>(1); day <= ci.ArgAt<DateOnly>(2); day = day.AddDays(1))
                {
                    map[day] = new Dictionary<Guid, EffectiveContractData> { [_clientId] = _contract };
                }

                return map;
            });

        var resolver = new ClientHolidayCalendarResolver(_context, new HolidayCalculatorCache());

        var exemptionRepository = Substitute.For<IHolidayWorkExemptionRuleRepository>();
        exemptionRepository.GetAllActiveAsync().Returns(_ => _exemptions.ToList());
        var enforcement = Substitute.For<IComplianceEnforcementResolver>();
        enforcement.GetModeAsync(Arg.Any<string>()).Returns(RuleEnforcementMode.Warn);
        _evaluator = new HolidayWorkEvaluator(exemptionRepository, resolver, contractData, enforcement);

        var weekConfiguration = Substitute.For<IWeekConfiguration>();
        weekConfiguration.GetWeekendDaysAsync().Returns(new HashSet<DayOfWeek> { DayOfWeek.Saturday, DayOfWeek.Sunday });
        _macroDataProvider = new MacroDataProvider(
            _context, resolver, contractData, Substitute.For<IWorkChangeEffectiveTimeService>(), weekConfiguration);

        var settingsReader = Substitute.For<ISettingsReader>();
        settingsReader.GetSettingsByTypesAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(ci => (IReadOnlyDictionary<string, string>)ci.Arg<IEnumerable<string>>()
                .Where(_settings.ContainsKey).ToDictionary(key => key, key => _settings[key]));

        var selectionRepository = Substitute.For<ICalendarSelectionRepository>();
        selectionRepository.GetNoTracking(Arg.Any<Guid>())
            .Returns(ci => _context.CalendarSelection.AsNoTracking().FirstOrDefault(s => s.Id == ci.Arg<Guid>()));
        selectionRepository.GetNoTrackingWithSelectedCalendars(Arg.Any<Guid>())
            .Returns(ci => _context.CalendarSelection.AsNoTracking().Include(s => s.SelectedCalendars)
                .FirstOrDefault(s => s.Id == ci.Arg<Guid>()));
        var settingsRepository = Substitute.For<ISettingsRepository>();
        settingsRepository.GetCalendarRuleList().Returns(_ => _context.CalendarRule.AsNoTracking().ToList());
        var workCoverageReader = Substitute.For<IClientWorkCoverageReader>();
        workCoverageReader.GetWorksTouchingAsync(Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(new List<Klacks.Api.Application.DTOs.Schedules.HolidayOutcomeWork>());

        _sut = new HolidayOutcomeDiagnosisService(
            contractData,
            resolver,
            new HolidayCalendarSourceResolver(settingsReader, selectionRepository),
            _evaluator,
            exemptionRepository,
            enforcement,
            selectionRepository,
            settingsRepository,
            Substitute.For<IContractRepository>(),
            Substitute.For<ISchedulingRuleRepository>(),
            workCoverageReader);
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public async Task OfficialAndPaid_SurchargeAndWarning()
    {
        await SeedContractSelectionAsync(reminderOverride: null, isMandatory: true, isPaid: true);

        var diagnosis = await AssertConsistentAsync(Christmas);

        diagnosis.SurchargeReason.ShouldBe(HolidayOutcomeReasonCodes.Applies);
        diagnosis.WarningReason.ShouldBe(HolidayOutcomeReasonCodes.Applies);
    }

    [Test]
    public async Task OfficialButNotMarkedForTimeSurcharge_WarningOnly()
    {
        await SeedContractSelectionAsync(reminderOverride: null, isMandatory: true, isPaid: false);

        var diagnosis = await AssertConsistentAsync(Christmas);

        diagnosis.SurchargeReason.ShouldBe(HolidayOutcomeReasonCodes.NotMarkedForTimeSurcharge);
        diagnosis.WarningReason.ShouldBe(HolidayOutcomeReasonCodes.Applies);
    }

    [Test]
    public async Task ReminderOnlyEntry_NeitherSurchargeNorWarning()
    {
        await SeedContractSelectionAsync(reminderOverride: false, isMandatory: true, isPaid: true, ruleCountry: ReminderCountry);

        var diagnosis = await AssertConsistentAsync(Christmas);

        diagnosis.DowngradedByReminderOnly.ShouldBeTrue();
        diagnosis.SurchargeReason.ShouldBe(HolidayOutcomeReasonCodes.ReminderOnlyEntry);
        diagnosis.WarningReason.ShouldBe(HolidayOutcomeReasonCodes.ReminderOnlyEntry);
    }

    [Test]
    public async Task RuleNotOfficial_NeitherSurchargeNorWarning()
    {
        await SeedContractSelectionAsync(reminderOverride: null, isMandatory: false, isPaid: true);

        var diagnosis = await AssertConsistentAsync(Christmas);

        diagnosis.SurchargeReason.ShouldBe(HolidayOutcomeReasonCodes.RuleNotOfficial);
        diagnosis.WarningReason.ShouldBe(HolidayOutcomeReasonCodes.RuleNotOfficial);
    }

    [Test]
    public async Task GlobalExemption_SuppressesOnlyTheWarning()
    {
        await SeedContractSelectionAsync(reminderOverride: null, isMandatory: true, isPaid: true);
        _exemptions.Add(new HolidayWorkExemptionRule { Id = Guid.NewGuid(), Description = "Spital" });

        var diagnosis = await AssertConsistentAsync(Christmas);

        diagnosis.SurchargeReason.ShouldBe(HolidayOutcomeReasonCodes.Applies);
        diagnosis.WarningReason.ShouldBe(HolidayOutcomeReasonCodes.ExemptionApplies);
        diagnosis.ExemptionDescription.ShouldBe("Spital");
    }

    [Test]
    public async Task ExemptionOfAnotherSchedulingRule_DoesNotApply()
    {
        await SeedContractSelectionAsync(reminderOverride: null, isMandatory: true, isPaid: true);
        _exemptions.Add(new HolidayWorkExemptionRule { Id = Guid.NewGuid(), Description = "Andere", SchedulingRuleId = Guid.NewGuid() });

        var diagnosis = await AssertConsistentAsync(Christmas);

        diagnosis.WarningReason.ShouldBe(HolidayOutcomeReasonCodes.Applies);
    }

    [Test]
    public async Task OrdinaryDay_IsNotAHoliday()
    {
        await SeedContractSelectionAsync(reminderOverride: null, isMandatory: true, isPaid: true);

        var diagnosis = await AssertConsistentAsync(Ordinary);

        diagnosis.SurchargeReason.ShouldBe(HolidayOutcomeReasonCodes.NotAHoliday);
        diagnosis.WarningReason.ShouldBe(HolidayOutcomeReasonCodes.NotAHoliday);
    }

    [Test]
    public async Task NoCalendarAnywhere_NoHolidayCalendar()
    {
        var diagnosis = await AssertConsistentAsync(Christmas);

        diagnosis.Calendar.Source.ShouldBe(HolidayCalendarSource.None);
        diagnosis.SurchargeReason.ShouldBe(HolidayOutcomeReasonCodes.NoHolidayCalendar);
        diagnosis.WarningReason.ShouldBe(HolidayOutcomeReasonCodes.NoHolidayCalendar);
    }

    [Test]
    public async Task CompanyCountryAndRegionFallback_UsesTheExactPairOnly()
    {
        SeedCompanySetting(SettingKeys.GlobalCalendarCountry, Country);
        SeedCompanySetting(SettingKeys.GlobalCalendarState, State);
        _context.CalendarRule.Add(Rule(Country, Country, isMandatory: true, isPaid: true));
        await _context.SaveChangesAsync();

        var diagnosis = await AssertConsistentAsync(Christmas);

        diagnosis.Calendar.Source.ShouldBe(HolidayCalendarSource.CompanyCountryState);
        diagnosis.SurchargeReason.ShouldBe(HolidayOutcomeReasonCodes.NotAHoliday);
    }

    [Test]
    public async Task CompanyDefaultSelection_IsUsedWithoutContractCalendar()
    {
        var selectionId = await SeedSelectionAsync(reminderOverride: null, isMandatory: true, isPaid: true, ruleCountry: Country);
        SeedCompanySetting(SettingKeys.GlobalCalendarSelectionId, selectionId.ToString());
        await _context.SaveChangesAsync();

        var diagnosis = await AssertConsistentAsync(Christmas);

        diagnosis.Calendar.Source.ShouldBe(HolidayCalendarSource.CompanyDefault);
        diagnosis.SurchargeReason.ShouldBe(HolidayOutcomeReasonCodes.Applies);
    }

    private async Task<Klacks.Api.Application.DTOs.Schedules.HolidayOutcomeDiagnosis> AssertConsistentAsync(DateOnly date)
    {
        var diagnosis = await _sut.DiagnoseAsync(_clientId, "Müller", date);

        var macroData = await _macroDataProvider.GetMacroDataAsync(new Work
        {
            Id = Guid.NewGuid(),
            ClientId = _clientId,
            ShiftId = Guid.NewGuid(),
            CurrentDate = date,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(16, 0),
            WorkTime = 8m,
        });
        var warnings = await _evaluator.EvaluateAsync(_clientId, "Müller", [date]);

        diagnosis.EarnsHolidayTimeSurcharge.ShouldBe(macroData.Holiday);
        diagnosis.HolidayWorkWarningRaised.ShouldBe(warnings.Count > 0);
        diagnosis.WarningReason.ShouldNotBe(HolidayOutcomeReasonCodes.Unexplained);
        return diagnosis;
    }

    private async Task SeedContractSelectionAsync(bool? reminderOverride, bool isMandatory, bool isPaid, string ruleCountry = Country)
    {
        var selectionId = await SeedSelectionAsync(reminderOverride, isMandatory, isPaid, ruleCountry);
        _contract = _contract with { CalendarSelectionId = selectionId };
    }

    private async Task<Guid> SeedSelectionAsync(bool? reminderOverride, bool isMandatory, bool isPaid, string ruleCountry)
    {
        var selectionId = Guid.NewGuid();
        _context.CalendarSelection.Add(new CalendarSelection
        {
            Id = selectionId,
            Name = "Bern + USA",
            SelectedCalendars =
            {
                new SelectedCalendar { Id = Guid.NewGuid(), CalendarSelectionId = selectionId, Country = Country, State = State },
                new SelectedCalendar
                {
                    Id = Guid.NewGuid(),
                    CalendarSelectionId = selectionId,
                    Country = ReminderCountry,
                    State = ReminderCountry,
                    OfficialOverride = reminderOverride,
                },
            },
        });
        _context.CalendarRule.Add(Rule(ruleCountry, ruleCountry == Country ? State : ReminderCountry, isMandatory, isPaid));
        await _context.SaveChangesAsync();
        return selectionId;
    }

    private void SeedCompanySetting(string key, string value)
    {
        _settings[key] = value;
        _context.Settings.Add(new SettingsEntity { Id = Guid.NewGuid(), Type = key, Value = value });
    }

    private static CalendarRule Rule(string country, string state, bool isMandatory, bool isPaid) => new()
    {
        Id = Guid.NewGuid(),
        Country = country,
        State = state,
        Name = new MultiLanguage { De = "Weihnachten", En = "Christmas Day" },
        Rule = ChristmasRule,
        SubRule = string.Empty,
        IsMandatory = isMandatory,
        IsPaid = isPaid,
    };
}
