// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Guard for the descriptions of the seeded non-statutory calendar rules (is_mandatory = false): the UI
/// marks those rules as "unofficial", and every one of them must explain why, in every shipped language.
/// The seeded rows are read from the SQL the two calendar rule seeds really emit (not from a copied id
/// list), so a new non-mandatory seed row without a description fails here. The languages are the core
/// languages plus every pack directory under Klacks.Api/Plugins/Languages, so a new language pack fails
/// here until the texts carry it.
/// </summary>

using System.Text.RegularExpressions;
using Klacks.Api.Data.Seed;
using Klacks.Api.Domain.Common;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Persistence.Seed;

[TestFixture]
public class UnofficialHolidayDescriptionsSeedGuardTests
{
    private const string ApiProjectDirectory = "Klacks.Api";
    private const string LanguagesRelativePath = "Plugins/Languages";
    private const string CalendarRuleInsert = "INSERT INTO public.calendar_rule";
    private const string CalendarRuleUpdate = "UPDATE public.calendar_rule SET description";

    /// <summary>
    /// Seed rows marked is_mandatory = false although the day IS a statutory holiday across the whole
    /// canton (Josefstag in NW, SZ, TI, UR, VS; Peter und Paul in TI). They deliberately carry no
    /// "unofficial" description; the is_mandatory flag needs an owner decision. Once a row is corrected
    /// to mandatory, KnownMisflaggedRows_AreStillNonMandatory fails so this list gets cleaned up.
    /// </summary>
    private static readonly string[] KnownMisflaggedOfficialHolidayIds =
    [
        "00319001-0001-0001-0001-000000000003",
        "00319001-0001-0001-0001-000000000005",
        "00319001-0001-0001-0001-000000000006",
        "00319001-0001-0001-0001-000000000007",
        "00319001-0001-0001-0001-000000000008",
        "00629001-0001-0001-0001-000000000002",
    ];

    private static readonly Regex SeedRow = new(
        @"\('(?<id>[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})',\s*'[^']*',\s*'[^']*',\s*(?<mandatory>true|false),",
        RegexOptions.Compiled);

    private static List<string> SqlOf(Action<MigrationBuilder> apply)
    {
        var builder = new MigrationBuilder(activeProvider: null);
        apply(builder);
        return builder.Operations.OfType<SqlOperation>().Select(o => o.Sql).ToList();
    }

    private static Dictionary<string, bool> SeededCalendarRules()
    {
        var statements = SqlOf(CalendarRulesSeed.SeedData).Concat(SqlOf(AdditionalCalendarRulesSeed.SeedData));
        var rows = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var statement in statements.Where(s => s.Contains(CalendarRuleInsert)))
        {
            foreach (Match match in SeedRow.Matches(statement))
            {
                rows.Add(match.Groups["id"].Value, match.Groups["mandatory"].Value == "true");
            }
        }

        return rows;
    }

    private static IReadOnlyList<string> ShippedLanguages()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var candidate = Path.Combine(directory.FullName, ApiProjectDirectory, LanguagesRelativePath);
            if (Directory.Exists(candidate))
            {
                return MultiLanguage.CoreLanguages
                    .Concat(new DirectoryInfo(candidate).GetDirectories().Select(d => d.Name.ToLowerInvariant()))
                    .Distinct()
                    .OrderBy(code => code, StringComparer.Ordinal)
                    .ToList();
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate {ApiProjectDirectory}/{LanguagesRelativePath} from the test base directory.");
    }

    private static Dictionary<string, IReadOnlyDictionary<string, string>> AssignedTexts() =>
        UnofficialHolidayDescriptionsSql.Assignments
            .SelectMany(a => a.Ids.Select(id => (id, a.Texts)))
            .ToDictionary(x => x.id, x => x.Texts, StringComparer.OrdinalIgnoreCase);

    [Test]
    public void SeedParsing_FindsTheKnownRows()
    {
        var rows = SeededCalendarRules();

        rows.Count.ShouldBeGreaterThan(300);
        rows["0ab12401-0001-0001-0001-000000000001"].ShouldBeFalse();
        rows["613c22be-e39f-4a40-be5a-e1202d21678f"].ShouldBeTrue();
        rows["100bb001-0001-0001-0001-000000000001"].ShouldBeFalse();
    }

    [Test]
    public void EveryNonMandatorySeedRow_HasADescription_UnlessKnownMisflagged()
    {
        var assigned = AssignedTexts();
        var missing = SeededCalendarRules()
            .Where(row => !row.Value)
            .Select(row => row.Key)
            .Where(id => !assigned.ContainsKey(id) && !KnownMisflaggedOfficialHolidayIds.Contains(id))
            .ToList();

        missing.ShouldBeEmpty($"Non-mandatory seed rows without description: {string.Join(", ", missing)}");
    }

    [Test]
    public void EveryDescribedId_IsANonMandatorySeedRow()
    {
        var rows = SeededCalendarRules();

        var invalid = AssignedTexts().Keys
            .Where(id => !rows.TryGetValue(id, out var mandatory) || mandatory)
            .ToList();

        invalid.ShouldBeEmpty($"Described ids that are unknown or mandatory: {string.Join(", ", invalid)}");
    }

    [Test]
    public void NoIdIsAssignedTwice()
    {
        var duplicates = UnofficialHolidayDescriptionsSql.Assignments
            .SelectMany(a => a.Ids)
            .GroupBy(id => id, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        duplicates.ShouldBeEmpty();
    }

    [Test]
    public void KnownMisflaggedRows_AreStillNonMandatory_AndCarryNoUnofficialText()
    {
        var rows = SeededCalendarRules();
        var assigned = AssignedTexts();

        foreach (var id in KnownMisflaggedOfficialHolidayIds)
        {
            rows.ShouldContainKey(id);
            rows[id].ShouldBeFalse($"{id} is mandatory now - remove it from {nameof(KnownMisflaggedOfficialHolidayIds)}.");
            assigned.ShouldNotContainKey(id);
        }
    }

    [Test]
    public void EveryText_CoversExactlyTheShippedLanguages_WithNonEmptyValues()
    {
        var languages = ShippedLanguages();
        languages.Count.ShouldBeGreaterThan(MultiLanguage.CoreLanguages.Length);

        foreach (var (texts, ids) in UnofficialHolidayDescriptionsSql.Assignments)
        {
            var keys = string.Join(",", texts.Keys.OrderBy(k => k, StringComparer.Ordinal));
            keys.ShouldBe(string.Join(",", languages), $"Language keys of the text for {ids[0]}");
            texts.Where(t => string.IsNullOrWhiteSpace(t.Value)).ShouldBeEmpty();
        }
    }

    [Test]
    public void NoTextFallsBackToEnglish()
    {
        foreach (var (texts, ids) in UnofficialHolidayDescriptionsSql.Assignments)
        {
            var english = texts["en"];
            var copies = texts.Where(t => t.Key != "en" && t.Value == english).Select(t => t.Key).ToList();
            copies.ShouldBeEmpty($"English copied into other languages for {ids[0]}");
        }
    }

    [Test]
    public void DataSeeder_WritesTheDescriptions_AfterBothCalendarRuleSeeds()
    {
        var statements = SqlOf(builder => DataSeeder.Add(builder));

        var lastInsert = statements.FindLastIndex(s => s.Contains(CalendarRuleInsert));
        var firstUpdate = statements.FindIndex(s => s.Contains(CalendarRuleUpdate));

        lastInsert.ShouldBeGreaterThanOrEqualTo(0);
        firstUpdate.ShouldBeGreaterThan(lastInsert);
    }

    [Test]
    public void Apply_OnlyFillsEmptyDescriptions_AndRemove_OnlyRevertsTheOwnText()
    {
        var apply = SqlOf(UnofficialHolidayDescriptionsSql.Apply);
        var remove = SqlOf(UnofficialHolidayDescriptionsSql.Remove);

        apply.Count.ShouldBe(UnofficialHolidayDescriptionsSql.Assignments.Count);
        remove.Count.ShouldBe(UnofficialHolidayDescriptionsSql.Assignments.Count);
        apply.ShouldAllBe(sql => sql.Contains("jsonb_each_text(description)") && sql.Contains("WHERE id IN ("));
        remove.ShouldAllBe(sql => sql.Contains("AND description = '") && sql.Contains("WHERE id IN ("));
    }
}
