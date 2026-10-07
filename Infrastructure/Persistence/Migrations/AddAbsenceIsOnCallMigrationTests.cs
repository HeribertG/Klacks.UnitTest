// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for the AddAbsenceIsOnCall migration: a non-nullable is_on_call column defaulting to false
/// (so the raw-SQL absence seeds keep working) and a backfill that flags exactly the seeded Pikett type.
/// The id is frozen here on purpose: a migration is history.
/// </summary>

using System.Text.RegularExpressions;
using Klacks.Api.Infrastructure.Persistence.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace Klacks.UnitTest.Infrastructure.Persistence.Migrations;

[TestFixture]
public class AddAbsenceIsOnCallMigrationTests
{
    private const string AbsenceTable = "absence";
    private const string OnCallColumn = "is_on_call";
    private const string BackfillMarker = "SET is_on_call = true";
    private const string FrozenPikettId = "5cb57c1c-ea82-455c-92c6-7920a0d6b19f";

    private static readonly Regex GuidLiteral =
        new(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", RegexOptions.Compiled);

    private static IReadOnlyList<MigrationOperation> Up() => new AddAbsenceIsOnCall().UpOperations;

    [Test]
    public void AddsNonNullableColumn_DefaultingToFalse()
    {
        var column = Up().OfType<AddColumnOperation>().Single(o => o.Table == AbsenceTable && o.Name == OnCallColumn);

        column.IsNullable.ShouldBeFalse();
        column.DefaultValue.ShouldBe(false);
    }

    [Test]
    public void Backfill_FlagsExactlyTheSeededPikettType()
    {
        var sql = Up().OfType<SqlOperation>().Single(o => o.Sql.Contains(BackfillMarker)).Sql;

        GuidLiteral.Matches(sql).Select(m => m.Value.ToLowerInvariant()).ShouldBe([FrozenPikettId]);
    }

    [Test]
    public void Backfill_RunsAfterTheColumnExists()
    {
        var operations = Up().ToList();

        operations.FindIndex(o => o is AddColumnOperation)
            .ShouldBeLessThan(operations.FindIndex(o => o is SqlOperation));
    }
}
