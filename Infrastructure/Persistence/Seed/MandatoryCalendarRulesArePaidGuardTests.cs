// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Guard for the paid flag of the shipped calendar rules: every statutory holiday (is_mandatory = true) must also
/// be paid (is_paid = true). The seeded rows are read from the SQL the two calendar rule seeds really emit, and
/// the rows StatutoryHolidayPromotionSql promotes to mandatory in a migration count as mandatory too, even when
/// an older seed state still carries false. The same invariant is checked for every language pack's
/// calendar-rules.json.
/// </summary>

using System.Text.Json;
using System.Text.RegularExpressions;
using Klacks.Api.Data.Seed;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using NUnit.Framework;
using Shouldly;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Infrastructure.Persistence.Seed;

[TestFixture]
public class MandatoryCalendarRulesArePaidGuardTests
{
    private const string ApiProjectDirectory = "Klacks.Api";
    private const string LanguagesRelativePath = "Plugins/Languages";
    private const string PackFileName = "calendar-rules.json";
    private const string CalendarRuleInsert = "INSERT INTO public.calendar_rule";
    private const string IdProperty = "id";
    private const string IsMandatoryProperty = "isMandatory";
    private const string IsPaidProperty = "isPaid";
    private const int MinimumSeededRowCount = 300;
    private const int MinimumPackRuleCount = 600;

    private static readonly Regex SeedRow = new(
        @"\('(?<id>[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})',\s*'[^']*',\s*'[^']*',\s*(?<mandatory>true|false),\s*(?<paid>true|false),",
        RegexOptions.Compiled);

    private sealed record SeedFlags(bool Mandatory, bool Paid);

    private static List<string> SqlOf(Action<MigrationBuilder> apply)
    {
        var builder = new MigrationBuilder(activeProvider: null);
        apply(builder);
        return builder.Operations.OfType<SqlOperation>().Select(o => o.Sql).ToList();
    }

    private static Dictionary<string, SeedFlags> SeededCalendarRules()
    {
        var statements = SqlOf(CalendarRulesSeed.SeedData).Concat(SqlOf(AdditionalCalendarRulesSeed.SeedData));
        var rows = new Dictionary<string, SeedFlags>(StringComparer.OrdinalIgnoreCase);
        foreach (var statement in statements.Where(s => s.Contains(CalendarRuleInsert)))
        {
            foreach (Match match in SeedRow.Matches(statement))
            {
                rows.Add(
                    match.Groups["id"].Value,
                    new SeedFlags(match.Groups["mandatory"].Value == "true", match.Groups["paid"].Value == "true"));
            }
        }

        return rows;
    }

    [Test]
    public void SeedParsing_ReadsBothFlagsOfTheKnownRows()
    {
        var rows = SeededCalendarRules();

        rows.Count.ShouldBeGreaterThan(MinimumSeededRowCount);
        rows["05a00001-0001-0001-0001-000000000001"].ShouldBe(new SeedFlags(true, true));
        rows["0ab12401-0001-0001-0001-000000000001"].Mandatory.ShouldBeFalse();
    }

    [Test]
    public void EveryMandatorySeedRow_IsPaid()
    {
        var promoted = new HashSet<string>(StatutoryHolidayPromotionSql.PromotedIds, StringComparer.OrdinalIgnoreCase);
        var rows = SeededCalendarRules();

        var mandatoryButUnpaid = rows
            .Where(row => (row.Value.Mandatory || promoted.Contains(row.Key)) && !row.Value.Paid)
            .Select(row => row.Key)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        mandatoryButUnpaid.ShouldBeEmpty();
    }

    [Test]
    public void EveryPromotedStatutoryHoliday_ExistsInTheSeedAndIsPaid()
    {
        var rows = SeededCalendarRules();

        foreach (var id in StatutoryHolidayPromotionSql.PromotedIds)
        {
            rows.ShouldContainKey(id);
            rows[id].Paid.ShouldBeTrue(id);
        }
    }

    [Test]
    public void EveryMandatoryLanguagePackRule_IsPaid()
    {
        var languagesDirectory = RepositoryRootLocator.RequireDirectory(ApiProjectDirectory, LanguagesRelativePath);
        var ruleCount = 0;
        var mandatoryButUnpaid = new List<string>();

        foreach (var packDirectory in new DirectoryInfo(languagesDirectory).GetDirectories())
        {
            var file = Path.Combine(packDirectory.FullName, PackFileName);
            if (!File.Exists(file))
            {
                continue;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(file));
            foreach (var rule in document.RootElement.EnumerateArray())
            {
                ruleCount++;
                if (rule.GetProperty(IsMandatoryProperty).GetBoolean() && !rule.GetProperty(IsPaidProperty).GetBoolean())
                {
                    mandatoryButUnpaid.Add($"{packDirectory.Name}:{rule.GetProperty(IdProperty).GetString()}");
                }
            }
        }

        ruleCount.ShouldBeGreaterThan(MinimumPackRuleCount);
        mandatoryButUnpaid.ShouldBeEmpty();
    }
}
