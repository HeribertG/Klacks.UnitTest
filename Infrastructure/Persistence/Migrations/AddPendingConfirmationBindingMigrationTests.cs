// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for the AddPendingConfirmationBinding migration: it adds exactly one nullable text column, binding_json, to
/// pending_confirmations (null means nothing is bound, so rows of older versions stay valid), touches nothing else, and
/// Down drops the column again. Table and column are frozen here on purpose: a migration is history.
/// </summary>

using Klacks.Api.Infrastructure.Persistence.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace Klacks.UnitTest.Infrastructure.Persistence.Migrations;

[TestFixture]
public class AddPendingConfirmationBindingMigrationTests
{
    private const string Table = "pending_confirmations";
    private const string Column = "binding_json";
    private const string TextType = "text";

    [Test]
    public void AddsOneNullableTextColumn()
    {
        var add = new AddPendingConfirmationBinding().UpOperations.ShouldHaveSingleItem().ShouldBeOfType<AddColumnOperation>();

        add.Table.ShouldBe(Table);
        add.Name.ShouldBe(Column);
        add.ColumnType.ShouldBe(TextType);
        add.IsNullable.ShouldBeTrue();
        add.DefaultValue.ShouldBeNull();
    }

    [Test]
    public void Down_DropsTheColumn()
    {
        var drop = new AddPendingConfirmationBinding().DownOperations.ShouldHaveSingleItem().ShouldBeOfType<DropColumnOperation>();

        drop.Table.ShouldBe(Table);
        drop.Name.ShouldBe(Column);
    }
}
