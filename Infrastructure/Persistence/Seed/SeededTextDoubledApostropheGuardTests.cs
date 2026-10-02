// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Guards the seeds against the escape error found 2026-10-02 (qualification names and the USA country name):
/// inside a SQL string literal one apostrophe is written as two, so four apostrophes store a doubled apostrophe
/// in the data ("d''Amerique"). The guard runs the seed SQL through a SQL literal decoder and fails when any
/// decoded literal still contains two adjacent apostrophes; a seed that really needs one writes two in the
/// source. Only seeds that write plain text/jsonb values are scanned (no scripts or templates).
/// </summary>

using System.Text;
using Klacks.Api.Data.Seed;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace Klacks.UnitTest.Infrastructure.Persistence.Seed;

[TestFixture]
public class SeededTextDoubledApostropheGuardTests
{
    private const char Quote = '\'';
    private const string DoubledApostrophe = "''";
    private const int ContextRadius = 40;

    private static IEnumerable<TestCaseData> Seeds()
    {
        yield return new TestCaseData(nameof(DefaultSeed), (Action<MigrationBuilder>)DefaultSeed.SeedData);
        yield return new TestCaseData(nameof(QualificationsSeed), (Action<MigrationBuilder>)QualificationsSeed.SeedData);
        yield return new TestCaseData(nameof(CalendarRulesSeed), (Action<MigrationBuilder>)CalendarRulesSeed.SeedData);
        yield return new TestCaseData(nameof(AdditionalCalendarRulesSeed), (Action<MigrationBuilder>)AdditionalCalendarRulesSeed.SeedData);
        yield return new TestCaseData(nameof(AbsencesSeed), (Action<MigrationBuilder>)AbsencesSeed.SeedData);
        yield return new TestCaseData(nameof(SwissZipSeed), (Action<MigrationBuilder>)SwissZipSeed.SeedData);
    }

    [TestCaseSource(nameof(Seeds))]
    public void NoDecodedSeedTextContainsADoubledApostrophe(string seedName, Action<MigrationBuilder> seed)
    {
        var builder = new MigrationBuilder(null);
        seed(builder);
        var sql = string.Concat(builder.Operations.OfType<SqlOperation>().Select(o => o.Sql + "\n"));

        var literals = DecodeLiterals(sql).ToList();

        literals.Count.ShouldBeGreaterThan(0, seedName);
        var faulty = literals.Where(l => l.Contains(DoubledApostrophe)).Select(Excerpt).ToList();
        faulty.ShouldBeEmpty($"{seedName} stores a doubled apostrophe: {string.Join(" | ", faulty.Take(5))}");
    }

    [Test]
    public void TheDecoder_ReadsTwoApostrophesAsOneAndFourAsTwo()
    {
        DecodeLiterals("VALUES ('d''Amerique', 'd''''Amerique', '')").ToList().ShouldBe(["d'Amerique", "d''Amerique", ""]);
    }

    private static string Excerpt(string literal)
    {
        var at = literal.IndexOf(DoubledApostrophe, StringComparison.Ordinal);
        var from = Math.Max(0, at - ContextRadius);
        return literal.Substring(from, Math.Min(literal.Length - from, 2 * ContextRadius));
    }

    private static IEnumerable<string> DecodeLiterals(string sql)
    {
        var current = new StringBuilder();
        var inside = false;
        for (var i = 0; i < sql.Length; i++)
        {
            var c = sql[i];
            if (!inside)
            {
                inside = c == Quote;
                continue;
            }

            if (c != Quote)
            {
                current.Append(c);
            }
            else if (i + 1 < sql.Length && sql[i + 1] == Quote)
            {
                current.Append(Quote);
                i++;
            }
            else
            {
                yield return current.ToString();
                current.Clear();
                inside = false;
            }
        }
    }
}
