// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The RetireHarmonizerLlmMode migration is a pure data fix: one update that moves a stored WIZARD3_MODE 'llm' to
/// 'deterministic', no schema change. Down is deliberately empty because the previous value is not known.
/// </summary>

using Klacks.Api.Infrastructure.Persistence.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace Klacks.UnitTest.Infrastructure.Persistence.Migrations;

[TestFixture]
public class RetireHarmonizerLlmModeMigrationTests
{
    [Test]
    public void Up_IsOneUpdateOfTheStoredModeWithoutSchemaChanges()
    {
        var operations = new RetireHarmonizerLlmMode().UpOperations;

        operations.Count.ShouldBe(1);
        var sql = operations.OfType<SqlOperation>().Single().Sql;
        sql.ShouldStartWith("UPDATE settings SET value = 'deterministic'");
        sql.ShouldContain("type = 'WIZARD3_MODE'");
        sql.ShouldContain("= 'llm'");
    }

    [Test]
    public void Down_ChangesNothing()
    {
        new RetireHarmonizerLlmMode().DownOperations.ShouldBeEmpty();
    }
}
