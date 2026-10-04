// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for the ContractFirstSurchargeResolution migration: contract.performs_shift_work becomes
/// nullable first, then the data fix keeps the current effective behaviour of existing contracts once the
/// contract wins over the scheduling rule. A contract value is cleared to NULL ("standard") only where a
/// live scheduling rule (or, for the rates, a live rate revision of that rule) defines the same field,
/// because today the rule wins there anyway. The guards are frozen here on purpose: a migration is history.
/// </summary>

using Klacks.Api.Infrastructure.Persistence.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace Klacks.UnitTest.Infrastructure.Persistence.Migrations;

[TestFixture]
public class ContractFirstSurchargeResolutionMigrationTests
{
    private static IReadOnlyList<MigrationOperation> Up() => new ContractFirstSurchargeResolution().UpOperations;

    private static IReadOnlyList<MigrationOperation> Down() => new ContractFirstSurchargeResolution().DownOperations;

    private static string UpSql() => string.Join("\n", Up().OfType<SqlOperation>().Select(o => o.Sql));

    [Test]
    public void MakesPerformsShiftWorkNullable_BeforeAnyDataStatement()
    {
        var operations = Up();

        var alter = operations[0].ShouldBeOfType<AlterColumnOperation>();
        alter.Table.ShouldBe("contract");
        alter.Name.ShouldBe("performs_shift_work");
        alter.IsNullable.ShouldBeTrue();
        alter.DefaultValue.ShouldBeNull();
        operations.Skip(1).ShouldAllBe(o => o is SqlOperation);
        operations.Count.ShouldBeGreaterThan(1);
    }

    [Test]
    public void ClearsPerformsShiftWork_OnlyWhereALiveRuleDefinesIt()
    {
        var sql = UpSql();

        sql.ShouldContain("SET performs_shift_work = NULL");
        sql.ShouldContain("r.performs_shift_work IS NOT NULL");
        sql.ShouldContain("c.performs_shift_work IS NOT NULL");
    }

    [TestCase("night_rate")]
    [TestCase("holiday_rate")]
    [TestCase("we1rate")]
    [TestCase("we2rate")]
    [TestCase("we3rate")]
    public void ClearsEachRate_OnlyWhereTheRuleOrALiveRevisionDefinesIt(string column)
    {
        var sql = UpSql();

        sql.ShouldContain($"SET {column} = NULL");
        sql.ShouldContain($"c.{column} IS NOT NULL");
        sql.ShouldContain($"r.{column} IS NOT NULL");
        sql.ShouldContain($"v.{column} IS NOT NULL");
    }

    [TestCase("r.id = c.scheduling_rule_id")]
    [TestCase("r.is_deleted = false")]
    [TestCase("FROM scheduling_rules r")]
    [TestCase("FROM scheduling_rule_rate_revisions v")]
    [TestCase("v.scheduling_rule_id = c.scheduling_rule_id")]
    [TestCase("v.is_deleted = false")]
    public void TouchesOnlyContractsBoundToALiveRuleOrRevision(string guard)
    {
        UpSql().ShouldContain(guard);
    }

    [Test]
    public void Down_FillsNullsBeforeRestoringNotNull()
    {
        var operations = Down();

        var fill = operations[0].ShouldBeOfType<SqlOperation>();
        fill.Sql.ShouldContain("SET performs_shift_work = false");
        fill.Sql.ShouldContain("performs_shift_work IS NULL");
        var alter = operations[1].ShouldBeOfType<AlterColumnOperation>();
        alter.Name.ShouldBe("performs_shift_work");
        alter.IsNullable.ShouldBeFalse();
    }
}
