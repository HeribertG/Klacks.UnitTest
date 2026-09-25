// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Guards that the macro seed of a fresh installation records its rows as templates: MacrosSeed runs after
/// every migration (DatabaseInitializer.SeedDataAsync), so the AddMacroOrigin backfill never reaches these
/// rows and each INSERT must write origin = Seed itself. Also keeps SeededMacroIds.All complete.
/// </summary>

using System.Text.RegularExpressions;
using Klacks.Api.Data.Seed;
using Klacks.Api.Domain.Constants;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace Klacks.UnitTest.Infrastructure.Persistence.Seed;

[TestFixture]
public class MacrosSeedOriginTests
{
    private const int SeededMacroRowCount = 8;
    private const int TemplateRowCountIncludingMigrationRow = 9;
    private const string OriginColumnListTail = "category,origin)";

    private static readonly Regex LeadingId =
        new(@"SELECT\s+'(?<id>[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})'", RegexOptions.Compiled);

    private static readonly Regex CategoryThenSeedOriginBeforeGuard =
        new(@",\s*\d+\s*,\s*1\s+WHERE NOT EXISTS", RegexOptions.Compiled);

    private static List<string> SeedStatements()
    {
        var builder = new MigrationBuilder(null);
        MacrosSeed.SeedData(builder);
        return builder.Operations.OfType<SqlOperation>().Select(o => o.Sql).ToList();
    }

    [Test]
    public void EveryMacroSeedInsert_ListsTheOriginColumnLast()
    {
        var statements = SeedStatements();

        statements.Count.ShouldBe(SeededMacroRowCount);
        statements.ShouldAllBe(sql => sql.Contains(OriginColumnListTail));
    }

    [Test]
    public void EveryMacroSeedInsert_WritesTheSeedOrigin()
    {
        SeedStatements().ShouldAllBe(sql => CategoryThenSeedOriginBeforeGuard.IsMatch(sql));
    }

    [Test]
    public void EveryMacroSeedId_IsListedInSeededMacroIds()
    {
        var seededIds = SeedStatements()
            .Select(sql => Guid.Parse(LeadingId.Match(sql).Groups["id"].Value))
            .ToList();

        seededIds.ShouldAllBe(id => SeededMacroIds.All.Contains(id));
        SeededMacroIds.All.Distinct().Count().ShouldBe(TemplateRowCountIncludingMigrationRow);
        SeededMacroIds.All.ShouldContain(SeededMacroIds.AllShiftAdditive);
    }
}
