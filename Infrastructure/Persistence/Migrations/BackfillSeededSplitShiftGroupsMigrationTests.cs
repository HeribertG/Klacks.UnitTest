// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for the BackfillSeededSplitShiftGroups migration: it is a pure data fix (one SQL statement,
/// no schema change) that copies the live group links of the original order to seeded split shifts, and
/// every guard that keeps it away from user data is present — only SplitShift rows, only the seeder's
/// actor name, never scenario or deleted rows, and only pieces that never held any group link (not even
/// a deleted one). The markers are frozen here on purpose: a migration is history.
/// </summary>

using Klacks.Api.Infrastructure.Persistence.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace Klacks.UnitTest.Infrastructure.Persistence.Migrations;

[TestFixture]
public class BackfillSeededSplitShiftGroupsMigrationTests
{
    private static IReadOnlyList<MigrationOperation> Up() => new BackfillSeededSplitShiftGroups().UpOperations;

    private static string Sql() => Up().OfType<SqlOperation>().Single().Sql;

    [Test]
    public void IsASingleDataStatement_WithoutSchemaChanges()
    {
        Up().ShouldHaveSingleItem().ShouldBeOfType<SqlOperation>();
    }

    [Test]
    public void CopiesTheLiveLinksOfTheOriginalOrder()
    {
        var sql = Sql();

        sql.ShouldContain("INSERT INTO group_item");
        sql.ShouldContain("link.shift_id = piece.original_id");
        sql.ShouldContain("link.is_deleted = false");
        sql.ShouldContain("link.analyse_token IS NULL");
        sql.ShouldContain("g.is_deleted = false");
    }

    [TestCase("piece.status = 3")]
    [TestCase("piece.is_deleted = false")]
    [TestCase("piece.analyse_token IS NULL")]
    [TestCase("piece.current_user_created = 'Anonymus'")]
    [TestCase("NOT EXISTS (SELECT 1 FROM group_item existing WHERE existing.shift_id = piece.id)")]
    public void TouchesOnlySeededSplitShiftsThatNeverHadAGroup(string guard)
    {
        Sql().ShouldContain(guard);
    }
}
