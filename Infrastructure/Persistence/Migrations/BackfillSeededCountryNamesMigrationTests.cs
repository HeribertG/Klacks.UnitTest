// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for the BackfillSeededCountryNames migration: a pure data fix (one UPDATE per faulty seeded
/// name, no schema change) that replaces a language key of countries.name only while it still equals the
/// faulty seed text exactly. The faulty texts are frozen on purpose (a migration is history); the corrected
/// texts must equal what the current DefaultSeed writes, so a fresh install and a repaired database end up
/// identical. Also guards that no seeded country or state name contains a doubled apostrophe again.
/// </summary>

using System.Text.Json;
using System.Text.RegularExpressions;
using Klacks.Api.Data.Seed;
using Klacks.Api.Infrastructure.Persistence.Migrations;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace Klacks.UnitTest.Infrastructure.Persistence.Migrations;

[TestFixture]
public class BackfillSeededCountryNamesMigrationTests
{
    private const int CorrectionCount = 2;
    private const string UsaId = "276e0392-bfa3-4230-b8a7-8e9fdfecad57";
    private const string DoubledApostrophe = "''";
    private const string CountryInsertMarker = "INSERT INTO public.countries";
    private const string StateInsertMarker = "INSERT INTO public.state ";

    private static readonly Regex SeedRow = new(
        @"\('(?<id>[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})',.*?'(?<json>\{.*?\})'::jsonb",
        RegexOptions.Compiled);

    private static IReadOnlyList<MigrationOperation> Up() => new BackfillSeededCountryNames().UpOperations;

    private static List<string> Statements() => Up().OfType<SqlOperation>().Select(o => o.Sql).ToList();

    private static Dictionary<string, JsonElement> SeedNames(string insertMarker)
    {
        var builder = new MigrationBuilder(null);
        DefaultSeed.SeedData(builder);
        var sql = builder.Operations.OfType<SqlOperation>().Select(o => o.Sql).Single(s => s.Contains(insertMarker));

        return SeedRow.Matches(sql).ToDictionary(
            m => m.Groups["id"].Value,
            m => JsonDocument.Parse(m.Groups["json"].Value.Replace(DoubledApostrophe, "'")).RootElement.Clone());
    }

    [Test]
    public void IsOneDataStatementPerCorrection_WithoutSchemaChanges()
    {
        var up = Up();

        up.Count.ShouldBe(CorrectionCount);
        up.ShouldAllBe(op => op is SqlOperation);
        CountryNameCorrectionSql.Corrections.Count.ShouldBe(CorrectionCount);
    }

    [Test]
    public void Down_IsEmpty_BecauseItWouldReintroduceTheFaultyTexts()
    {
        new BackfillSeededCountryNames().DownOperations.ShouldBeEmpty();
    }

    [Test]
    public void EveryStatement_ReplacesOnlyAnExactMatchOfTheFaultySeedText()
    {
        var statements = Statements();

        for (var i = 0; i < CountryNameCorrectionSql.Corrections.Count; i++)
        {
            var correction = CountryNameCorrectionSql.Corrections[i];
            var sql = statements[i];

            sql.ShouldStartWith("UPDATE countries SET name = jsonb_set(name, '{" + correction.Language + "}'");
            sql.ShouldContain($"WHERE id = '{correction.RowId}'");
            sql.ShouldContain($"AND name ->> '{correction.Language}' = '{correction.FaultyName.Replace("'", DoubledApostrophe)}';");
            sql.ShouldContain($"to_jsonb('{correction.CorrectedName.Replace("'", DoubledApostrophe)}'::text)");
        }
    }

    [Test]
    public void TheFaultyTexts_AreTheUsaNamesWithADoubledApostrophe()
    {
        CountryNameCorrectionSql.Corrections.ShouldAllBe(c => c.RowId == UsaId);
        CountryNameCorrectionSql.Corrections.Select(c => c.Language).ShouldBe(["fr", "it"]);
        CountryNameCorrectionSql.Corrections.ShouldAllBe(c => c.FaultyName.Contains(DoubledApostrophe));
        CountryNameCorrectionSql.Corrections.ShouldAllBe(c => c.CorrectedName == c.FaultyName.Replace(DoubledApostrophe, "'"));
    }

    [Test]
    public void TheCorrectedTexts_EqualWhatTheCurrentSeedWrites()
    {
        var seed = SeedNames(CountryInsertMarker);

        foreach (var correction in CountryNameCorrectionSql.Corrections)
        {
            seed.ShouldContainKey(correction.RowId);
            seed[correction.RowId].GetProperty(correction.Language).GetString().ShouldBe(correction.CorrectedName);
        }
    }

    [Test]
    public void TheSeed_NoLongerContainsAFaultyText_OrADoubledApostropheInAnyCountryOrStateName()
    {
        foreach (var marker in new[] { CountryInsertMarker, StateInsertMarker })
        {
            var seed = SeedNames(marker);

            seed.Count.ShouldBeGreaterThan(CorrectionCount);
            foreach (var (id, name) in seed)
            {
                foreach (var language in name.EnumerateObject())
                {
                    language.Value.GetString()!.Contains(DoubledApostrophe).ShouldBeFalse($"{marker} {id} / {language.Name}");
                }
            }
        }

        var countries = SeedNames(CountryInsertMarker);
        foreach (var correction in CountryNameCorrectionSql.Corrections)
        {
            countries[correction.RowId].GetProperty(correction.Language).GetString().ShouldNotBe(correction.FaultyName);
        }
    }
}
