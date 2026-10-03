// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for the AddPersonalAccessTokenAccessMode migration: the column default is Read (0, secure by
/// default for new rows), and every row that existed before the migration is backfilled to Write (1), so
/// integrations created before access modes existed keep their write access.
/// </summary>

using Klacks.Api.Infrastructure.Persistence.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace Klacks.UnitTest.Infrastructure.Persistence.Migrations;

[TestFixture]
public class AddPersonalAccessTokenAccessModeMigrationTests
{
    private const string TableName = "personal_access_tokens";
    private const string ColumnName = "access_mode";

    private static IReadOnlyList<MigrationOperation> Up() => new AddPersonalAccessTokenAccessMode().UpOperations;

    [Test]
    public void Up_AddsNonNullableColumnWithReadDefault()
    {
        var addColumn = Up().OfType<AddColumnOperation>().Single();

        addColumn.Table.ShouldBe(TableName);
        addColumn.Name.ShouldBe(ColumnName);
        addColumn.IsNullable.ShouldBeFalse();
        addColumn.DefaultValue.ShouldBe((int)PersonalAccessTokenAccessMode.Read);
    }

    [Test]
    public void Up_BackfillsEveryExistingRowToWrite_AfterAddingTheColumn()
    {
        var up = Up();
        var sql = up.OfType<SqlOperation>().Single();

        up.ToList().IndexOf(sql).ShouldBeGreaterThan(up.ToList().FindIndex(op => op is AddColumnOperation));
        sql.Sql.ShouldBe($"UPDATE {TableName} SET {ColumnName} = {(int)PersonalAccessTokenAccessMode.Write};");
    }

    [Test]
    public void Down_DropsTheColumn()
    {
        var drop = new AddPersonalAccessTokenAccessMode().DownOperations.OfType<DropColumnOperation>().Single();

        drop.Table.ShouldBe(TableName);
        drop.Name.ShouldBe(ColumnName);
    }
}
