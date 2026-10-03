// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for the BackfillSeededBerchtoldstagNames migration: a pure data fix (one UPDATE per seeded
/// Berchtoldstag rule and corrected language, no schema change) that replaces a language key of calendar_rule.name
/// only while it still equals the faulty "Saint Berchtold" text exactly. The faulty texts are frozen on purpose (a
/// migration is history); the corrected texts must equal what a fresh install writes - CalendarRulesSeed for
/// English, the master file default-calendar-rule-translations.json for the plugin languages - so a fresh install
/// and a repaired database end up identical. German, French and Italian are not touched.
/// </summary>

using System.Text.Json;
using System.Text.RegularExpressions;
using Klacks.Api.Data.Seed;
using Klacks.Api.Infrastructure.Persistence.Migrations;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace Klacks.UnitTest.Infrastructure.Persistence.Migrations;

[TestFixture]
public class BackfillSeededBerchtoldstagNamesMigrationTests
{
    private const int RuleCount = 13;
    private const int CorrectedLanguageCount = 21;
    private const string RuleIdPrefix = "b0102001-0001-0001-0001-";
    private const string English = "en";
    private const string DoubledApostrophe = "''";
    private const string Apostrophe = "'";
    private const string MasterFileName = "default-calendar-rule-translations.json";
    private const string CalendarRulesProperty = "calendarRules";
    private const string IdsProperty = "ids";
    private const string NameProperty = "name";

    private static readonly string[] UntouchedLanguages = ["de", "fr", "it"];
    private static readonly string[] PluginsLanguagesRelativePath = ["Klacks.Api", "Plugins", "Languages"];

    private static readonly Regex SeedRow = new(
        "\\('(?<id>[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})', '[^']*', '[^']*', (?:true|false), (?:true|false), "
        + "'[^']*', '[^']*', '(?:[^']|'')*', '(?<name>(?:[^']|'')*)'\\)",
        RegexOptions.Compiled);

    private static IReadOnlyList<MigrationOperation> Up() => new BackfillSeededBerchtoldstagNames().UpOperations;

    [Test]
    public void IsOneDataStatementPerRuleAndLanguage_WithoutSchemaChanges()
    {
        var up = Up();

        CalendarRuleNameCorrectionSql.BerchtoldstagRuleIds.Count.ShouldBe(RuleCount);
        CalendarRuleNameCorrectionSql.BerchtoldstagNames.Count.ShouldBe(CorrectedLanguageCount);
        CalendarRuleNameCorrectionSql.Corrections.Count.ShouldBe(RuleCount * CorrectedLanguageCount);
        up.Count.ShouldBe(RuleCount * CorrectedLanguageCount);
        up.ShouldAllBe(op => op is SqlOperation);
    }

    [Test]
    public void Down_IsEmpty_BecauseItWouldReintroduceTheFaultyTexts()
    {
        new BackfillSeededBerchtoldstagNames().DownOperations.ShouldBeEmpty();
    }

    [Test]
    public void EveryStatement_ReplacesOnlyAnExactMatchOfTheFaultyText()
    {
        var statements = Up().OfType<SqlOperation>().Select(o => o.Sql).ToList();

        for (var i = 0; i < CalendarRuleNameCorrectionSql.Corrections.Count; i++)
        {
            var correction = CalendarRuleNameCorrectionSql.Corrections[i];
            var sql = statements[i];

            sql.ShouldStartWith("UPDATE calendar_rule SET name = jsonb_set(name, '{" + correction.Language + "}'");
            sql.ShouldContain($"WHERE id = '{correction.RowId}'");
            sql.ShouldContain($"AND name ->> '{correction.Language}' = '{correction.FaultyName.Replace(Apostrophe, DoubledApostrophe)}';");
            sql.ShouldContain($"to_jsonb('{correction.CorrectedName.Replace(Apostrophe, DoubledApostrophe)}'::text)");
        }
    }

    [Test]
    public void TheCorrections_CoverExactlyTheBerchtoldstagRules_AndLeaveGermanFrenchAndItalianAlone()
    {
        CalendarRuleNameCorrectionSql.BerchtoldstagRuleIds.ShouldAllBe(id => id.StartsWith(RuleIdPrefix));
        CalendarRuleNameCorrectionSql.BerchtoldstagRuleIds.Distinct().Count().ShouldBe(RuleCount);
        CalendarRuleNameCorrectionSql.BerchtoldstagNames.Select(n => n.Language).Distinct().Count().ShouldBe(CorrectedLanguageCount);
        CalendarRuleNameCorrectionSql.Corrections.ShouldAllBe(c => !UntouchedLanguages.Contains(c.Language));
        CalendarRuleNameCorrectionSql.Corrections.ShouldAllBe(c => c.FaultyName != c.CorrectedName);
        CalendarRuleNameCorrectionSql.Corrections.ShouldAllBe(c => !c.FaultyName.Contains(DoubledApostrophe));
    }

    [Test]
    public void TheCorrectedEnglishName_EqualsWhatTheCurrentSeedWrites()
    {
        var seed = SeedNames();
        var english = CalendarRuleNameCorrectionSql.BerchtoldstagNames.Single(n => n.Language == English);

        foreach (var id in CalendarRuleNameCorrectionSql.BerchtoldstagRuleIds)
        {
            seed.ShouldContainKey(id);
            seed[id].GetProperty(English).GetString().ShouldBe(english.CorrectedName);
        }
    }

    [Test]
    public void TheCorrectedPluginLanguageNames_EqualWhatTheMasterFileMerges()
    {
        var master = MasterNames();

        foreach (var id in CalendarRuleNameCorrectionSql.BerchtoldstagRuleIds)
        {
            master.ShouldContainKey(id);
            foreach (var name in CalendarRuleNameCorrectionSql.BerchtoldstagNames.Where(n => n.Language != English))
            {
                master[id].ShouldContainKey(name.Language);
                master[id][name.Language].ShouldBe(name.CorrectedName, $"{id} / {name.Language}");
            }
        }
    }

    [Test]
    public void NeitherTheSeedNorTheMasterFile_StillContainsAFaultyText()
    {
        var seed = SeedNames();
        var master = MasterNames();

        foreach (var correction in CalendarRuleNameCorrectionSql.Corrections)
        {
            seed[correction.RowId].EnumerateObject().Select(p => p.Value.GetString()).ShouldNotContain(correction.FaultyName);
            master[correction.RowId].Values.ShouldNotContain(correction.FaultyName);
        }
    }

    private static Dictionary<string, JsonElement> SeedNames()
    {
        var builder = new MigrationBuilder(activeProvider: null);
        CalendarRulesSeed.SeedData(builder);
        var sql = string.Concat(builder.Operations.OfType<SqlOperation>().Select(o => o.Sql));

        return SeedRow.Matches(sql).ToDictionary(
            m => m.Groups["id"].Value,
            m => JsonDocument.Parse(m.Groups[NameProperty].Value.Replace(DoubledApostrophe, Apostrophe)).RootElement.Clone());
    }

    private static Dictionary<string, Dictionary<string, string>> MasterNames()
    {
        var path = Path.Combine(LocateDirectory(PluginsLanguagesRelativePath), MasterFileName);
        using var document = JsonDocument.Parse(File.ReadAllText(path));

        var result = new Dictionary<string, Dictionary<string, string>>();
        foreach (var entry in document.RootElement.GetProperty(CalendarRulesProperty).EnumerateArray())
        {
            var names = entry.GetProperty(NameProperty).EnumerateObject()
                .ToDictionary(p => p.Name, p => p.Value.GetString() ?? string.Empty);
            foreach (var id in entry.GetProperty(IdsProperty).EnumerateArray())
            {
                result[id.GetString()!] = names;
            }
        }

        return result;
    }

    private static string LocateDirectory(string[] relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(relativePath).ToArray());
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate {string.Join('/', relativePath)} from {AppContext.BaseDirectory}");
    }
}
