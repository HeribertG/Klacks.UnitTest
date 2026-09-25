// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the AddMacroAssignmentHistory migration: it creates the macro_assignment_history table with the columns of
/// the history entity (switch id, holder kind and id, macro before and after, the user, the undo links and the audit
/// fields), without any foreign key (the history outlives deleted macros, shifts and absence types), with one index over
/// holder kind and id and one over the switch id, touches nothing else, and Down drops the table again. Table, column and
/// index columns are frozen here on purpose: a migration is history.
/// </summary>

using Klacks.Api.Infrastructure.Persistence.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace Klacks.UnitTest.Infrastructure.Persistence.Migrations;

[TestFixture]
public class AddMacroAssignmentHistoryMigrationTests
{
    private const string Table = "macro_assignment_history";
    private const string ColumnSeparator = ",";

    private static readonly string[] FrozenColumns =
    [
        "id", "switch_id", "target", "target_id", "previous_macro_id", "new_macro_id", "changed_by_user_id",
        "revert_of_history_id", "reverted_by_history_id", "create_time", "current_user_created",
        "current_user_deleted", "current_user_updated", "deleted_time", "is_deleted", "update_time"
    ];

    private static readonly string[] FrozenIndexes = ["target,target_id", "switch_id"];

    private static IReadOnlyList<MigrationOperation> Up() => new AddMacroAssignmentHistory().UpOperations;

    private static CreateTableOperation CreateTable() =>
        Up().OfType<CreateTableOperation>().Single(o => o.Name == Table);

    [Test]
    public void CreatesTheHistoryTable_WithEveryColumn()
    {
        CreateTable().Columns.Select(c => c.Name).ShouldBe(FrozenColumns, ignoreOrder: true);
    }

    [Test]
    public void HasNoForeignKey()
    {
        CreateTable().ForeignKeys.ShouldBeEmpty();
    }

    [Test]
    public void IndexesTheHolderAndTheSwitch()
    {
        Up().OfType<CreateIndexOperation>()
            .Where(o => o.Table == Table)
            .Select(o => string.Join(ColumnSeparator, o.Columns))
            .ShouldBe(FrozenIndexes, ignoreOrder: true);
    }

    [Test]
    public void TouchesNothingElse()
    {
        Up().Select(o => o.GetType()).Distinct()
            .ShouldBe(new[] { typeof(CreateTableOperation), typeof(CreateIndexOperation) }, ignoreOrder: true);
    }

    [Test]
    public void Down_DropsTheTable()
    {
        new AddMacroAssignmentHistory().DownOperations.OfType<DropTableOperation>().Single().Name.ShouldBe(Table);
    }
}
