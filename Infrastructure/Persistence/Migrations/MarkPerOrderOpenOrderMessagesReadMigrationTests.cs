// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for the MarkPerOrderOpenOrderMessagesRead migration: one UPDATE that stamps read_at_utc on the
/// proactive inbox rows of the former per-order open-order shape only (kind open_order AND content key
/// i18n:assistant.proactive.openOrder), never on the daily summary (assistant.proactive.openOrdersSummary), only on
/// rows still unread (idempotent), and never deletes anything. The literals are frozen here on purpose: a migration
/// is history.
/// </summary>

using Klacks.Api.Infrastructure.Persistence.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace Klacks.UnitTest.Infrastructure.Persistence.Migrations;

[TestFixture]
public class MarkPerOrderOpenOrderMessagesReadMigrationTests
{
    private static string UpSql() =>
        new MarkPerOrderOpenOrderMessagesRead().UpOperations.OfType<SqlOperation>().Single().Sql;

    [Test]
    public void Up_IsOneSqlUpdateOfTheDispatchTable_AndDeletesNothing()
    {
        var operations = new MarkPerOrderOpenOrderMessagesRead().UpOperations;
        var sql = UpSql();

        operations.ShouldHaveSingleItem();
        sql.ShouldContain("UPDATE agent_trigger_dispatches");
        sql.ShouldContain("SET read_at_utc = now()");
        sql.ShouldNotContain("DELETE", Case.Insensitive);
        sql.ShouldNotContain("is_deleted", Case.Insensitive);
    }

    [Test]
    public void Up_IsScopedToThePerOrderMessagesOnly_NeverTheSummary()
    {
        var sql = UpSql();

        sql.ShouldContain("trigger_kind = 'open_order'");
        sql.ShouldContain("content_key = 'i18n:assistant.proactive.openOrder'");
        sql.ShouldNotContain("openOrdersSummary");
        sql.ShouldNotContain("LIKE", Case.Insensitive);
    }

    [Test]
    public void Up_TouchesOnlyUnreadRows_SoRunningItAgainChangesNothing()
    {
        UpSql().ShouldContain("AND read_at_utc IS NULL");
    }

    [Test]
    public void Down_IsANoOp()
    {
        new MarkPerOrderOpenOrderMessagesRead().DownOperations.ShouldBeEmpty();
    }
}
