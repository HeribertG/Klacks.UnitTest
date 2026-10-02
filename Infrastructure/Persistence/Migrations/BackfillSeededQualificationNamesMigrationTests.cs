// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for the BackfillSeededQualificationNames migration: a pure data fix (one UPDATE per faulty
/// seeded name, no schema change) that replaces a language key of qualification.name only while it still
/// equals the faulty seed text exactly. The faulty texts are frozen on purpose (a migration is history); the
/// corrected texts must equal what the current QualificationsSeed writes, so a fresh install and a repaired
/// database end up identical. Also guards that no seeded name contains a doubled apostrophe again.
/// </summary>

using System.Text.Json;
using System.Text.RegularExpressions;
using Klacks.Api.Data.Seed;
using Klacks.Api.Infrastructure.Persistence.Migrations;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace Klacks.UnitTest.Infrastructure.Persistence.Migrations;

[TestFixture]
public class BackfillSeededQualificationNamesMigrationTests
{
    private const int CorrectionCount = 6;
    private const int SeededQualificationCount = 114;
    private const string DoubledApostrophe = "''";

    private static readonly Regex SeedRow = new(
        @"\('(?<id>[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})',\s*'(?<json>\{.*?\})'::jsonb",
        RegexOptions.Compiled);

    private static IReadOnlyList<MigrationOperation> Up() => new BackfillSeededQualificationNames().UpOperations;

    private static List<string> Statements() => Up().OfType<SqlOperation>().Select(o => o.Sql).ToList();

    private static Dictionary<string, JsonElement> SeedNames()
    {
        var builder = new MigrationBuilder(null);
        QualificationsSeed.SeedData(builder);
        var sql = string.Concat(builder.Operations.OfType<SqlOperation>().Select(o => o.Sql));

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
        QualificationNameCorrectionSql.Corrections.Count.ShouldBe(CorrectionCount);
    }

    [Test]
    public void Down_IsEmpty_BecauseItWouldReintroduceTheFaultyTexts()
    {
        new BackfillSeededQualificationNames().DownOperations.ShouldBeEmpty();
    }

    [Test]
    public void EveryStatement_ReplacesOnlyAnExactMatchOfTheFaultySeedText()
    {
        var statements = Statements();

        for (var i = 0; i < QualificationNameCorrectionSql.Corrections.Count; i++)
        {
            var correction = QualificationNameCorrectionSql.Corrections[i];
            var sql = statements[i];

            sql.ShouldStartWith("UPDATE qualification SET name = jsonb_set(name, '{" + correction.Language + "}'");
            sql.ShouldContain($"WHERE id = '{correction.QualificationId}'");
            sql.ShouldContain($"AND name ->> '{correction.Language}' = '{correction.FaultyName.Replace("'", DoubledApostrophe)}';");
            sql.ShouldContain($"to_jsonb('{correction.CorrectedName.Replace("'", DoubledApostrophe)}'::text)");
        }
    }

    [Test]
    public void TheFaultyTexts_AreTheDoubledApostropheAndTheFrenchItalianName()
    {
        var doubled = QualificationNameCorrectionSql.Corrections.Where(c => c.FaultyName.Contains(DoubledApostrophe)).ToList();

        doubled.Count.ShouldBe(CorrectionCount - 1);
        doubled.ShouldAllBe(c => c.CorrectedName == c.FaultyName.Replace(DoubledApostrophe, "'"));

        var coldChain = QualificationNameCorrectionSql.Corrections.Single(c => !c.FaultyName.Contains(DoubledApostrophe));
        coldChain.Language.ShouldBe("it");
        coldChain.FaultyName.ShouldBe("Gestion de la chaîne du froid");
        coldChain.CorrectedName.ShouldBe("Gestione della catena del freddo");
    }

    [Test]
    public void TheCorrectedTexts_EqualWhatTheCurrentSeedWrites()
    {
        var seed = SeedNames();

        foreach (var correction in QualificationNameCorrectionSql.Corrections)
        {
            seed.ShouldContainKey(correction.QualificationId);
            seed[correction.QualificationId].GetProperty(correction.Language).GetString().ShouldBe(correction.CorrectedName);
        }
    }

    [Test]
    public void TheSeed_NoLongerContainsAFaultyText_OrADoubledApostropheInAnyName()
    {
        var seed = SeedNames();

        seed.Count.ShouldBeGreaterThanOrEqualTo(SeededQualificationCount);
        foreach (var (id, name) in seed)
        {
            foreach (var language in name.EnumerateObject())
            {
                language.Value.GetString()!.Contains(DoubledApostrophe).ShouldBeFalse($"{id} / {language.Name}");
            }
        }

        foreach (var correction in QualificationNameCorrectionSql.Corrections)
        {
            seed[correction.QualificationId].GetProperty(correction.Language).GetString().ShouldNotBe(correction.FaultyName);
        }
    }

    [Test]
    public void TheCorrectedItalianColdChainName_DiffersFromTheFrenchOne()
    {
        var coldChain = SeedNames()["0c562163-931d-4982-bfcc-0388bec2ef9f"];

        coldChain.GetProperty("it").GetString().ShouldNotBe(coldChain.GetProperty("fr").GetString());
    }
}
