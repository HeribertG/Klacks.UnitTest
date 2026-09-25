// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the AddMacroOrigin migration: it adds a non-nullable origin column that defaults to
/// User (0), marks exactly the nine template ids frozen in this test as Seed, and marks the remaining rows
/// that carry a region-setup import key as Import — in that order, so a template row is never classified as
/// an import. The expected ids and origin values are frozen here on purpose: a migration is history, so a
/// later change to SeededMacroIds or MacroOrigin must not require rewriting it.
/// </summary>

using System.Text.RegularExpressions;
using Klacks.Api.Infrastructure.Persistence.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace Klacks.UnitTest.Infrastructure.Persistence.Migrations;

[TestFixture]
public class AddMacroOriginMigrationTests
{
    private const string MacroTable = "macro";
    private const string OriginColumn = "origin";
    private const int FrozenUserOriginValue = 0;
    private const string SeedBackfillMarker = "SET origin = 1";
    private const string ImportBackfillMarker = "SET origin = 2";
    private const string ImportKeyCondition = "import_source_key <> ''";
    private const string StillUserCondition = "origin = 0";

    private static readonly string[] FrozenTemplateIds =
    [
        "b1481e19-eaba-458a-a33b-666f2ecc28d2",
        "ac8a7b05-2312-41aa-a21d-e3edba54aef5",
        "a3edd3f5-c31c-4746-a9a0-c613d14ffd23",
        "e4a71d2c-5b8f-4c3a-9d16-84f0b2a7c9e3",
        "ad86380e-3e8e-4497-95c1-3555ee0803c4",
        "f7704df2-bb51-40c8-9ecd-ad57c1064490",
        "9f2b4c67-3d1a-4e85-b7c9-5a8d0e6f2b31",
        "3bac9e54-4368-4174-8bc9-435ce08aecbd",
        "7c5a9d21-4e8b-4f3a-9c67-2d1e8f5b0a43"
    ];

    private static readonly Regex GuidLiteral =
        new(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", RegexOptions.Compiled);

    private static IReadOnlyList<MigrationOperation> Up() => new AddMacroOrigin().UpOperations;

    private static List<string> SqlStatements() => Up().OfType<SqlOperation>().Select(o => o.Sql).ToList();

    [Test]
    public void AddsNonNullableOriginColumn_DefaultingToUser()
    {
        var column = Up().OfType<AddColumnOperation>().Single(o => o.Table == MacroTable && o.Name == OriginColumn);

        column.IsNullable.ShouldBeFalse();
        column.DefaultValue.ShouldBe(FrozenUserOriginValue);
    }

    [Test]
    public void MarksExactlyTheFrozenTemplateIds_AsSeed()
    {
        var seedSql = SqlStatements().Single(sql => sql.Contains(SeedBackfillMarker));

        var idsInSql = GuidLiteral.Matches(seedSql).Select(match => match.Value.ToLowerInvariant()).ToList();

        idsInSql.ShouldBe(FrozenTemplateIds, ignoreOrder: true);
    }

    [Test]
    public void MarksOnlyRemainingImportKeyedRows_AsImport()
    {
        var importSql = SqlStatements().Single(sql => sql.Contains(ImportBackfillMarker));

        importSql.ShouldContain(ImportKeyCondition);
        importSql.ShouldContain(StillUserCondition);
    }

    [Test]
    public void SeedBackfill_RunsBeforeImportBackfill()
    {
        var statements = SqlStatements();

        statements.FindIndex(sql => sql.Contains(SeedBackfillMarker))
            .ShouldBeLessThan(statements.FindIndex(sql => sql.Contains(ImportBackfillMarker)));
    }
}
