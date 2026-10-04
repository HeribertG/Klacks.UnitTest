// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Guard for the descriptions of the seeded non-statutory calendar rules (is_mandatory = false): the UI
/// marks those rules as "unofficial", and every one of them must explain why, in every shipped language.
/// The seeded rows are read from the SQL the two calendar rule seeds really emit (not from a copied id
/// list), so a new non-mandatory seed row without a description fails here. The languages are the core
/// languages plus every pack directory under Klacks.Api/Plugins/Languages, so a new language pack fails
/// here until the texts carry it.
/// </summary>

using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Klacks.Api.Data.Seed;
using Klacks.Api.Domain.Common;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using NUnit.Framework;
using Shouldly;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Infrastructure.Persistence.Seed;

[TestFixture]
public class UnofficialHolidayDescriptionsSeedGuardTests
{
    private const string ApiProjectDirectory = "Klacks.Api";
    private const string LanguagesRelativePath = "Plugins/Languages";
    private const string CalendarRuleInsert = "INSERT INTO public.calendar_rule";
    private const string CalendarRuleUpdate = "UPDATE public.calendar_rule SET description";
    private const string UsFederalHolidayIdPrefix = "05a00001-";
    private const int UsFederalHolidayCount = 11;
    private const int ShippedAddUnofficialHolidayDescriptionsIdCount = 83;
    private const int ShippedAddUnofficialHolidayDescriptionsStatementCount = 14;
    private const int ShippedAddUnofficialHolidayDescriptionsSqlLength = 42253;
    private const string ShippedAddUnofficialHolidayDescriptionsSqlSha256 =
        "8900AFF4413328AB09B09110CB7A669D8630F39F729BC76C40812E8C72F2752F";
    private const string StatementSeparator = "\n";
    private static readonly Regex SqlId = new(
        @"'(?<id>[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})'::uuid",
        RegexOptions.Compiled);

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
        var languagesDirectory = RepositoryRootLocator.RequireDirectory(ApiProjectDirectory, LanguagesRelativePath);

        return MultiLanguage.CoreLanguages
            .Concat(new DirectoryInfo(languagesDirectory).GetDirectories().Select(d => d.Name.ToLowerInvariant()))
            .Distinct()
            .OrderBy(code => code, StringComparer.Ordinal)
            .ToList();
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
    public void EveryNonMandatorySeedRow_HasADescription()
    {
        var assigned = AssignedTexts();
        var missing = SeededCalendarRules()
            .Where(row => !row.Value)
            .Select(row => row.Key)
            .Where(id => !assigned.ContainsKey(id))
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
    public void PromotedStatutoryHolidays_AreMandatoryInTheSeed_AndCarryNoUnofficialText()
    {
        var rows = SeededCalendarRules();
        var assigned = AssignedTexts();

        StatutoryHolidayPromotionSql.PromotedIds.Count.ShouldBe(12);
        foreach (var id in StatutoryHolidayPromotionSql.PromotedIds)
        {
            rows.ShouldContainKey(id);
            rows[id].ShouldBeTrue($"{id} is statutory and must be seeded with is_mandatory = true.");
            assigned.ShouldNotContainKey(id);
        }
    }

    [Test]
    public void AllElevenUsFederalHolidays_AreMandatoryInTheSeed()
    {
        var usFederal = SeededCalendarRules()
            .Where(row => row.Key.StartsWith(UsFederalHolidayIdPrefix, StringComparison.OrdinalIgnoreCase))
            .ToList();

        usFederal.Count.ShouldBe(UsFederalHolidayCount);
        usFederal.Where(row => !row.Value).Select(row => row.Key).ShouldBeEmpty();
    }

    [Test]
    public void AddUnofficialHolidayDescriptionsMigration_KeepsItsShippedAssignments()
    {
        var frozen = UnofficialHolidayDescriptionsSql.AddUnofficialHolidayDescriptionsAssignments;

        frozen.Count.ShouldBe(UnofficialHolidayDescriptionsSql.Assignments.Count + 1);
        frozen.Last().Texts.ShouldBeSameAs(UnofficialHolidayDescriptionTexts.UsFederalHoliday);
        frozen.Last().Ids.ShouldBe(StatutoryHolidayPromotionSql.UsFederalHolidayIds);
        frozen.SelectMany(a => a.Ids).Count().ShouldBe(ShippedAddUnofficialHolidayDescriptionsIdCount);
    }

    /// <summary>
    /// The expected values were computed from UnofficialHolidayDescriptionsSql.cs and
    /// UnofficialHolidayDescriptionTexts.cs as shipped in Klacks.Api commit 8045f33eb (Apply then Remove,
    /// statements joined by a line feed), so the already applied 20260929120000 migration keeps emitting
    /// byte-identical SQL in both directions.
    /// </summary>
    [Test]
    public void AddUnofficialHolidayDescriptionsMigration_EmitsTheShippedSqlByteForByte()
    {
        var frozen = UnofficialHolidayDescriptionsSql.AddUnofficialHolidayDescriptionsAssignments;
        var statements = SqlOf(builder => UnofficialHolidayDescriptionsSql.Apply(builder, frozen))
            .Concat(SqlOf(builder => UnofficialHolidayDescriptionsSql.Remove(builder, frozen)))
            .ToList();
        var sql = string.Join(StatementSeparator, statements);

        statements.Count.ShouldBe(ShippedAddUnofficialHolidayDescriptionsStatementCount);
        sql.Length.ShouldBe(ShippedAddUnofficialHolidayDescriptionsSqlLength);
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql))).ShouldBe(ShippedAddUnofficialHolidayDescriptionsSqlSha256);
    }

    [Test]
    public void Promotion_UpdatesOnlyThePromotedIds_AndClearsOnlyTheExactGeneratedText()
    {
        var apply = SqlOf(StatutoryHolidayPromotionSql.Apply);
        var remove = SqlOf(StatutoryHolidayPromotionSql.Remove);
        var usText = CalendarRuleDescriptionSql.SqlLiteral(UnofficialHolidayDescriptionsSql.DescriptionJson(UnofficialHolidayDescriptionTexts.UsFederalHoliday));

        apply.Count.ShouldBe(2);
        remove.Count.ShouldBe(2);
        apply[0].ShouldContain("SET is_mandatory = true WHERE id IN (");
        apply[0].ShouldEndWith("AND is_mandatory = false;");
        remove[0].ShouldContain("SET is_mandatory = false WHERE id IN (");
        remove[0].ShouldEndWith("AND is_mandatory = true;");
        apply[1].ShouldContain($"AND description = '{usText}'::jsonb");
        remove[1].ShouldContain("jsonb_each_text(description)");
        apply.Concat(remove).ShouldAllBe(sql => !sql.Contains("is_paid"));

        foreach (var sql in apply.Concat(remove))
        {
            var ids = SqlId.Matches(sql).Select(m => m.Groups["id"].Value).ToList();
            ids.ShouldNotBeEmpty();
            ids.ShouldAllBe(id => StatutoryHolidayPromotionSql.PromotedIds.Contains(id));
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
