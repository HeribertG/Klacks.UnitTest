// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Proves the startup calendar rule heal only fills gaps the pre-v1.0.36 installer left by an id collision: it
/// inserts a pack rule only when its current or pre-renumbering id is held by another country's rule, never
/// touches existing rows, never revives a rule without that footprint, and runs exactly once per pack.
/// </summary>

using Klacks.Api.Application.Constants;
using Klacks.Api.Domain.Models.Settings;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.Settings;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Settings;

[TestFixture]
public class LanguagePluginCalendarRuleBackfillerTests
{
    private const string PolishCode = "pl";
    private const string NorwegianCode = "nb";
    private const string Poland = "PL";
    private const string Norway = "NO";
    private const string NewYear = "New Year's Day";
    private const string NorwegianHoliday = "Constitution Day";
    private const string UnreadableJson = "{ not json";
    private const string PolishNewYear = "Nowy Rok";
    private const string PolishNewYearDescription = "Pierwszy dzien roku";
    private const string PolishConstitutionDay = "Dzien Konstytucji Norwegii";
    private const string PolishConstitutionDayDescription = "Norweskie swieto narodowe";
    private const string NorwegianConstitutionDay = "Grunnlovsdagen";
    private const string NorwegianNewYear = "Nyttarsdag";
    private const string PolishMarkerKey =LanguagePluginConstants.CalendarRuleBackfillSettingPrefix + "PL";

    private static readonly Guid LegacyId = Guid.Parse("d1c2d3e4-bb01-4000-8000-000000000001");
    private static readonly Guid PackId = Guid.Parse("6fb8057e-2489-4838-9485-0d1a2f4e1218");

    private string _pluginDirectory = null!;
    private DataBaseContext _context = null!;
    private IServiceScope _scope = null!;
    private LanguagePluginCalendarRuleBackfiller _backfiller = null!;

    [SetUp]
    public void Setup()
    {
        _pluginDirectory = Path.Combine(Path.GetTempPath(), "klacks-calendar-backfill-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_pluginDirectory, PolishCode));
        Directory.CreateDirectory(Path.Combine(_pluginDirectory, NorwegianCode));

        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _context.Database.EnsureCreated();

        var provider = Substitute.For<IServiceProvider>();
        provider.GetService(typeof(DataBaseContext)).Returns(_context);
        _scope = Substitute.For<IServiceScope>();
        _scope.ServiceProvider.Returns(provider);

        _backfiller = new LanguagePluginCalendarRuleBackfiller(_pluginDirectory, NullLogger.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _scope.Dispose();
        _context.Database.EnsureDeleted();
        _context.Dispose();

        if (Directory.Exists(_pluginDirectory))
        {
            Directory.Delete(_pluginDirectory, recursive: true);
        }
    }

    [Test]
    public async Task Backfill_LegacyIdHeldByOtherCountry_InsertsMissingRuleUnderPackId()
    {
        await GivenRuleAsync(LegacyId, Norway, NorwegianHoliday);
        GivenPackFile(PolishCode, LanguagePluginConstants.CalendarRulesFileName, RuleJson(PackId, Poland, NewYear));
        GivenPackFile(PolishCode, LanguagePluginConstants.CalendarRuleLegacyIdsFileName, LegacyJson(PackId, LegacyId));

        await _backfiller.BackfillMissingCalendarRulesAsync(_scope, PolishCode);

        var rules = await _context.CalendarRule.AsNoTracking().ToListAsync();
        rules.Count.ShouldBe(2);
        var owner = rules.Single(r => r.Id == LegacyId);
        owner.Country.ShouldBe(Norway);
        owner.Name.En.ShouldBe(NorwegianHoliday);
        var healed = rules.Single(r => r.Country == Poland);
        healed.Id.ShouldBe(PackId);
        healed.Name.En.ShouldBe(NewYear);
    }

    [Test]
    public async Task Backfill_CurrentIdHeldByOtherCountry_InsertsMissingRuleUnderFreshId()
    {
        await GivenRuleAsync(LegacyId, Poland, NewYear);
        GivenPackFile(NorwegianCode, LanguagePluginConstants.CalendarRulesFileName, RuleJson(LegacyId, Norway, NorwegianHoliday));

        await _backfiller.BackfillMissingCalendarRulesAsync(_scope, NorwegianCode);

        var rules = await _context.CalendarRule.AsNoTracking().ToListAsync();
        rules.Count.ShouldBe(2);
        rules.Single(r => r.Id == LegacyId).Country.ShouldBe(Poland);
        rules.Single(r => r.Country == Norway).Id.ShouldNotBe(LegacyId);
    }

    [Test]
    public async Task Backfill_NoCollisionFootprint_DoesNotReviveRule()
    {
        GivenPackFile(PolishCode, LanguagePluginConstants.CalendarRulesFileName, RuleJson(PackId, Poland, NewYear));
        GivenPackFile(PolishCode, LanguagePluginConstants.CalendarRuleLegacyIdsFileName, LegacyJson(PackId, LegacyId));

        await _backfiller.BackfillMissingCalendarRulesAsync(_scope, PolishCode);

        (await _context.CalendarRule.AsNoTracking().CountAsync()).ShouldBe(0);
        (await MarkerExistsAsync()).ShouldBeTrue();
    }

    [Test]
    public async Task Backfill_IdHeldBySameCountryRenamedRule_DoesNotInsertDuplicate()
    {
        await GivenRuleAsync(PackId, Poland, "Renamed by customer");
        GivenPackFile(PolishCode, LanguagePluginConstants.CalendarRulesFileName, RuleJson(PackId, Poland, NewYear));

        await _backfiller.BackfillMissingCalendarRulesAsync(_scope, PolishCode);

        var rules = await _context.CalendarRule.AsNoTracking().ToListAsync();
        rules.Count.ShouldBe(1);
        rules[0].Name.En.ShouldBe("Renamed by customer");
    }

    [Test]
    public async Task Backfill_RuleAlreadyPresentByNaturalKey_IsLeftUntouched()
    {
        const string customerRule = "01/02";
        var existingId = Guid.NewGuid();
        await GivenRuleAsync(LegacyId, Norway, NorwegianHoliday);
        await GivenRuleAsync(existingId, Poland, NewYear, customerRule);
        GivenPackFile(PolishCode, LanguagePluginConstants.CalendarRulesFileName, RuleJson(PackId, Poland, NewYear));
        GivenPackFile(PolishCode, LanguagePluginConstants.CalendarRuleLegacyIdsFileName, LegacyJson(PackId, LegacyId));

        await _backfiller.BackfillMissingCalendarRulesAsync(_scope, PolishCode);

        var rules = await _context.CalendarRule.AsNoTracking().ToListAsync();
        rules.Count.ShouldBe(2);
        var polish = rules.Single(r => r.Country == Poland);
        polish.Id.ShouldBe(existingId);
        polish.Rule.ShouldBe(customerRule);
    }

    [Test]
    public async Task Backfill_RuleWithoutEnglishName_IsSkipped()
    {
        await GivenRuleAsync(LegacyId, Norway, NorwegianHoliday);
        GivenPackFile(PolishCode, LanguagePluginConstants.CalendarRulesFileName, RuleJson(LegacyId, Poland, string.Empty));

        await _backfiller.BackfillMissingCalendarRulesAsync(_scope, PolishCode);

        (await _context.CalendarRule.AsNoTracking().CountAsync()).ShouldBe(1);
    }

    [Test]
    public async Task Backfill_LegacyFileMissing_StillHealsByCurrentIdAndSetsMarker()
    {
        await GivenRuleAsync(LegacyId, Norway, NorwegianHoliday);
        GivenPackFile(PolishCode, LanguagePluginConstants.CalendarRulesFileName, RuleJson(LegacyId, Poland, NewYear));

        await _backfiller.BackfillMissingCalendarRulesAsync(_scope, PolishCode);

        (await _context.CalendarRule.AsNoTracking().AnyAsync(r => r.Country == Poland)).ShouldBeTrue();
        (await MarkerExistsAsync()).ShouldBeTrue();
    }

    [Test]
    public async Task Backfill_LegacyFileUnreadable_InsertsNothingAndSetsNoMarker()
    {
        await GivenRuleAsync(LegacyId, Norway, NorwegianHoliday);
        GivenPackFile(PolishCode, LanguagePluginConstants.CalendarRulesFileName, RuleJson(PackId, Poland, NewYear));
        GivenPackFile(PolishCode, LanguagePluginConstants.CalendarRuleLegacyIdsFileName, UnreadableJson);

        await _backfiller.BackfillMissingCalendarRulesAsync(_scope, PolishCode);

        (await _context.CalendarRule.AsNoTracking().CountAsync()).ShouldBe(1);
        (await MarkerExistsAsync()).ShouldBeFalse();
    }

    [Test]
    public async Task Backfill_LegacyFileWithDuplicateIds_InsertsNothingAndSetsNoMarker()
    {
        await GivenRuleAsync(LegacyId, Norway, NorwegianHoliday);
        GivenPackFile(PolishCode, LanguagePluginConstants.CalendarRulesFileName, RuleJson(PackId, Poland, NewYear));
        GivenPackFile(PolishCode, LanguagePluginConstants.CalendarRuleLegacyIdsFileName,
            $$"""[ { "id": "{{PackId}}", "legacyId": "{{LegacyId}}" }, { "id": "{{PackId}}", "legacyId": "{{Guid.NewGuid()}}" } ]""");

        await _backfiller.BackfillMissingCalendarRulesAsync(_scope, PolishCode);

        (await _context.CalendarRule.AsNoTracking().CountAsync()).ShouldBe(1);
        (await MarkerExistsAsync()).ShouldBeFalse();
    }

    [Test]
    public async Task Backfill_RulesFileUnreadable_SetsNoMarker_AndHealsOnceTheFileIsFixed()
    {
        await GivenRuleAsync(LegacyId, Norway, NorwegianHoliday);
        GivenPackFile(PolishCode, LanguagePluginConstants.CalendarRulesFileName, UnreadableJson);
        GivenPackFile(PolishCode, LanguagePluginConstants.CalendarRuleLegacyIdsFileName, LegacyJson(PackId, LegacyId));

        await _backfiller.BackfillMissingCalendarRulesAsync(_scope, PolishCode);
        (await MarkerExistsAsync()).ShouldBeFalse();

        GivenPackFile(PolishCode, LanguagePluginConstants.CalendarRulesFileName, RuleJson(PackId, Poland, NewYear));
        await _backfiller.BackfillMissingCalendarRulesAsync(_scope, PolishCode);

        (await _context.CalendarRule.AsNoTracking().AnyAsync(r => r.Id == PackId)).ShouldBeTrue();
        (await MarkerExistsAsync()).ShouldBeTrue();
    }

    [Test]
    public async Task Backfill_MarkerPresent_DoesNothing()
    {
        await GivenRuleAsync(LegacyId, Norway, NorwegianHoliday);
        _context.Settings.Add(new Klacks.Api.Domain.Models.Settings.Settings
        {
            Id = Guid.NewGuid(),
            Type = PolishMarkerKey,
            Value = LanguagePluginConstants.CalendarRuleBackfillDoneValue
        });
        await _context.SaveChangesAsync();
        GivenPackFile(PolishCode, LanguagePluginConstants.CalendarRulesFileName, RuleJson(PackId, Poland, NewYear));
        GivenPackFile(PolishCode, LanguagePluginConstants.CalendarRuleLegacyIdsFileName, LegacyJson(PackId, LegacyId));

        await _backfiller.BackfillMissingCalendarRulesAsync(_scope, PolishCode);

        (await _context.CalendarRule.AsNoTracking().CountAsync()).ShouldBe(1);
    }

    [Test]
    public async Task Backfill_RuleDeletedAfterHeal_IsNotRevivedOnNextStart()
    {
        await GivenRuleAsync(LegacyId, Norway, NorwegianHoliday);
        GivenPackFile(PolishCode, LanguagePluginConstants.CalendarRulesFileName, RuleJson(PackId, Poland, NewYear));
        GivenPackFile(PolishCode, LanguagePluginConstants.CalendarRuleLegacyIdsFileName, LegacyJson(PackId, LegacyId));
        await _backfiller.BackfillMissingCalendarRulesAsync(_scope, PolishCode);
        var healed = await _context.CalendarRule.SingleAsync(r => r.Id == PackId);
        _context.CalendarRule.Remove(healed);
        await _context.SaveChangesAsync();

        await _backfiller.BackfillMissingCalendarRulesAsync(_scope, PolishCode);

        (await _context.CalendarRule.AsNoTracking().AnyAsync(r => r.Country == Poland)).ShouldBeFalse();
        (await _context.Settings.AsNoTracking().CountAsync(s => s.Type == PolishMarkerKey)).ShouldBe(1);
    }

    [Test]
    public async Task MarkCalendarRulesInstalled_StagesMarkerOnce_SoAFreshInstallIsNeverHealed()
    {
        await _backfiller.MarkCalendarRulesInstalledAsync(_scope, PolishCode);
        await _context.SaveChangesAsync();
        await _backfiller.MarkCalendarRulesInstalledAsync(_scope, PolishCode);
        await _context.SaveChangesAsync();

        await GivenRuleAsync(LegacyId, Norway, NorwegianHoliday);
        GivenPackFile(PolishCode, LanguagePluginConstants.CalendarRulesFileName, RuleJson(PackId, Poland, NewYear));
        GivenPackFile(PolishCode, LanguagePluginConstants.CalendarRuleLegacyIdsFileName, LegacyJson(PackId, LegacyId));
        await _backfiller.BackfillMissingCalendarRulesAsync(_scope, PolishCode);

        (await _context.Settings.AsNoTracking().CountAsync(s => s.Type == PolishMarkerKey)).ShouldBe(1);
        (await _context.CalendarRule.AsNoTracking().AnyAsync(r => r.Country == Poland)).ShouldBeFalse();
    }

    [Test]
    public async Task Repair_LegacyIdDirection_PollutedValuesAreReplacedByTheHoldersOwnPackText()
    {
        await GivenTranslatedRuleAsync(LegacyId, Norway, NorwegianHoliday, PolishCode, PolishNewYear, PolishNewYearDescription);
        GivenPackFile(PolishCode, LanguagePluginConstants.CalendarRulesFileName,
            TranslatedRuleJson(PackId, Poland, NewYear, PolishCode, PolishNewYear, PolishNewYearDescription));
        GivenPackFile(PolishCode, LanguagePluginConstants.CalendarRuleLegacyIdsFileName, LegacyJson(PackId, LegacyId));
        GivenPackFile(NorwegianCode, LanguagePluginConstants.CalendarRulesFileName,
            TranslatedRuleJson(Guid.NewGuid(), Norway, NorwegianHoliday, PolishCode, PolishConstitutionDay, PolishConstitutionDayDescription));

        await _backfiller.BackfillMissingCalendarRulesAsync(_scope, PolishCode);

        var holder = await _context.CalendarRule.AsNoTracking().SingleAsync(r => r.Id == LegacyId);
        holder.Name.GetValue(PolishCode).ShouldBe(PolishConstitutionDay);
        holder.Description.GetValue(PolishCode).ShouldBe(PolishConstitutionDayDescription);
        holder.Name.En.ShouldBe(NorwegianHoliday);
    }

    [Test]
    public async Task Repair_PollutedValueWithoutOwnSource_RemovesTheLanguageKey()
    {
        await GivenTranslatedRuleAsync(LegacyId, Norway, NorwegianHoliday, PolishCode, PolishNewYear, PolishNewYearDescription);
        GivenPackFile(PolishCode, LanguagePluginConstants.CalendarRulesFileName,
            TranslatedRuleJson(PackId, Poland, NewYear, PolishCode, PolishNewYear, PolishNewYearDescription));
        GivenPackFile(PolishCode, LanguagePluginConstants.CalendarRuleLegacyIdsFileName, LegacyJson(PackId, LegacyId));

        await _backfiller.BackfillMissingCalendarRulesAsync(_scope, PolishCode);

        var holder = await _context.CalendarRule.AsNoTracking().SingleAsync(r => r.Id == LegacyId);
        holder.Name.GetValue(PolishCode).ShouldBeNull();
        holder.Description.GetValue(PolishCode).ShouldBeNull();
        holder.Name.En.ShouldBe(NorwegianHoliday);
    }

    [Test]
    public async Task Repair_CurrentIdDirection_PollutedValueIsReplacedByTheHoldersOwnPackText()
    {
        await GivenTranslatedRuleAsync(LegacyId, Poland, NewYear, NorwegianCode, NorwegianConstitutionDay, string.Empty);
        GivenPackFile(NorwegianCode, LanguagePluginConstants.CalendarRulesFileName,
            TranslatedRuleJson(LegacyId, Norway, NorwegianHoliday, NorwegianCode, NorwegianConstitutionDay, string.Empty));
        GivenPackFile(PolishCode, LanguagePluginConstants.CalendarRulesFileName,
            TranslatedRuleJson(PackId, Poland, NewYear, NorwegianCode, NorwegianNewYear, string.Empty));

        await _backfiller.BackfillMissingCalendarRulesAsync(_scope, NorwegianCode);

        var holder = await _context.CalendarRule.AsNoTracking().SingleAsync(r => r.Id == LegacyId);
        holder.Name.GetValue(NorwegianCode).ShouldBe(NorwegianNewYear);
    }

    [Test]
    public async Task Repair_ValueChangedByCustomer_IsLeftUntouched()
    {
        const string customerValue = "Dzien Konstytucji (wlasny)";
        await GivenTranslatedRuleAsync(LegacyId, Norway, NorwegianHoliday, PolishCode, customerValue, customerValue);
        GivenPackFile(PolishCode, LanguagePluginConstants.CalendarRulesFileName,
            TranslatedRuleJson(PackId, Poland, NewYear, PolishCode, PolishNewYear, PolishNewYearDescription));
        GivenPackFile(PolishCode, LanguagePluginConstants.CalendarRuleLegacyIdsFileName, LegacyJson(PackId, LegacyId));
        GivenPackFile(NorwegianCode, LanguagePluginConstants.CalendarRulesFileName,
            TranslatedRuleJson(Guid.NewGuid(), Norway, NorwegianHoliday, PolishCode, PolishConstitutionDay, PolishConstitutionDayDescription));

        await _backfiller.BackfillMissingCalendarRulesAsync(_scope, PolishCode);

        var holder = await _context.CalendarRule.AsNoTracking().SingleAsync(r => r.Id == LegacyId);
        holder.Name.GetValue(PolishCode).ShouldBe(customerValue);
        holder.Description.GetValue(PolishCode).ShouldBe(customerValue);
    }

    [Test]
    public async Task Repair_NoCollision_LeavesEqualTextOfAnUnrelatedRuleAlone()
    {
        var unrelatedId = Guid.NewGuid();
        await GivenTranslatedRuleAsync(unrelatedId, Norway, NorwegianHoliday, PolishCode, PolishNewYear, PolishNewYearDescription);
        GivenPackFile(PolishCode, LanguagePluginConstants.CalendarRulesFileName,
            TranslatedRuleJson(PackId, Poland, NewYear, PolishCode, PolishNewYear, PolishNewYearDescription));
        GivenPackFile(PolishCode, LanguagePluginConstants.CalendarRuleLegacyIdsFileName, LegacyJson(PackId, LegacyId));

        await _backfiller.BackfillMissingCalendarRulesAsync(_scope, PolishCode);

        var rule = await _context.CalendarRule.AsNoTracking().SingleAsync(r => r.Id == unrelatedId);
        rule.Name.GetValue(PolishCode).ShouldBe(PolishNewYear);
        rule.Description.GetValue(PolishCode).ShouldBe(PolishNewYearDescription);
    }

    [Test]
    public async Task Repair_MarkerPresent_LeavesPollutionAlone()
    {
        await GivenTranslatedRuleAsync(LegacyId, Norway, NorwegianHoliday, PolishCode, PolishNewYear, PolishNewYearDescription);
        await _backfiller.MarkCalendarRulesInstalledAsync(_scope, PolishCode);
        await _context.SaveChangesAsync();
        GivenPackFile(PolishCode, LanguagePluginConstants.CalendarRulesFileName,
            TranslatedRuleJson(PackId, Poland, NewYear, PolishCode, PolishNewYear, PolishNewYearDescription));
        GivenPackFile(PolishCode, LanguagePluginConstants.CalendarRuleLegacyIdsFileName, LegacyJson(PackId, LegacyId));

        await _backfiller.BackfillMissingCalendarRulesAsync(_scope, PolishCode);

        var holder = await _context.CalendarRule.AsNoTracking().SingleAsync(r => r.Id == LegacyId);
        holder.Name.GetValue(PolishCode).ShouldBe(PolishNewYear);
    }

    private async Task GivenTranslatedRuleAsync(
        Guid id, string country, string englishName, string language, string name, string description)
    {
        var names = new MultiLanguage();
        names.SetValue("en", englishName);
        names.SetValue(language, name);
        var descriptions = new MultiLanguage();
        descriptions.SetValue(language, description);
        _context.CalendarRule.Add(new CalendarRule
        {
            Id = id, Country = country, State = country, Rule = "01/01", Name = names, Description = descriptions
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    private static string TranslatedRuleJson(
        Guid id, string country, string englishName, string language, string name, string description) =>
        $$"""
        [ { "id": "{{id}}", "rule": "01/01", "subRule": "", "isMandatory": true, "isPaid": true,
            "state": "{{country}}", "country": "{{country}}",
            "name": { "en": "{{englishName}}", "{{language}}": "{{name}}" },
            "description": { "en": "", "{{language}}": "{{description}}" } } ]
        """;

    private async Task GivenRuleAsync(Guid id, string country, string englishName, string rule = "01/01")
    {
        var name = new MultiLanguage();
        name.SetValue("en", englishName);
        _context.CalendarRule.Add(new CalendarRule { Id = id, Country = country, State = country, Rule = rule, Name = name });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    private Task<bool> MarkerExistsAsync() =>
        _context.Settings.AsNoTracking().AnyAsync(s => s.Type == PolishMarkerKey);

    private static string RuleJson(Guid id, string country, string englishName) =>
        $$"""
        [ { "id": "{{id}}", "rule": "01/01", "subRule": "", "isMandatory": true, "isPaid": true,
            "state": "{{country}}", "country": "{{country}}", "name": { "en": "{{englishName}}" }, "description": { "en": "" } } ]
        """;

    private static string LegacyJson(Guid id, Guid legacyId) =>
        $$"""[ { "id": "{{id}}", "legacyId": "{{legacyId}}" } ]""";

    private void GivenPackFile(string code, string fileName, string json)
    {
        File.WriteAllText(Path.Combine(_pluginDirectory, code, fileName), json);
    }
}
