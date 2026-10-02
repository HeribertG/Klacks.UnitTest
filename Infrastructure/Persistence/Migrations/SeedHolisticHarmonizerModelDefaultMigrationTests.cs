// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The SeedHolisticHarmonizerModelDefault migration is a pure data fix: exactly the insert and the update
/// of HolisticHarmonizerModelDefaultSql, no schema change. Down is deliberately empty - after the update
/// the migration cannot tell a replaced legacy value from a model the customer picked later.
/// </summary>

using Klacks.Api.Infrastructure.Persistence.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace Klacks.UnitTest.Infrastructure.Persistence.Migrations;

[TestFixture]
public class SeedHolisticHarmonizerModelDefaultMigrationTests
{
    [Test]
    public void Up_IsTwoDataStatementsWithoutSchemaChanges()
    {
        var operations = new SeedHolisticHarmonizerModelDefault().UpOperations;

        operations.Count.ShouldBe(2);
        operations.ShouldAllBe(o => o is SqlOperation);
    }

    [Test]
    public void Up_InsertsTheDefaultAndReplacesTheLegacyValue()
    {
        var sql = new SeedHolisticHarmonizerModelDefault().UpOperations.OfType<SqlOperation>().Select(o => o.Sql).ToList();

        sql[0].ShouldStartWith("INSERT INTO settings");
        sql[0].ShouldContain("'WIZARD3_LLM_MODEL', 'gemini-25-flash'");
        sql[1].ShouldStartWith("UPDATE settings SET value = 'gemini-25-flash'");
        sql[1].ShouldContain("value = 'gemini-35-flash'");
    }

    [Test]
    public void Down_ChangesNothing()
    {
        new SeedHolisticHarmonizerModelDefault().DownOperations.ShouldBeEmpty();
    }
}
