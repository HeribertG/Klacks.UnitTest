// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for the DisableSeededForumsysDirectoryLogin migration and the seed it corrects: one UPDATE that switches
/// off authentication and the enabled flag of the seeded public Forumsys test provider only (matched by its seeded id
/// AND its seeded host, so a row an administrator pointed at another server is left alone), deletes nothing, and has no
/// Down. The seed itself no longer enables the provider, so a fresh installation never sends passwords to the public
/// test server. The literals are frozen here on purpose: a migration is history.
/// </summary>

using System.Text.RegularExpressions;
using Klacks.Api.Data.Seed.IdentityProviders;
using Klacks.Api.Infrastructure.Persistence.Migrations;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace Klacks.UnitTest.Infrastructure.Persistence.Migrations;

[TestFixture]
public class DisableSeededForumsysDirectoryLoginMigrationTests
{
    private const string NpgsqlProvider = "Npgsql.EntityFrameworkCore.PostgreSQL";
    private const string FrozenUpSql =
        "UPDATE identity_providers SET use_for_authentication = false, is_enabled = false " +
        "WHERE id = 'fc0b8e9c-4694-40c1-a10e-fdb66a10b26e' AND host = 'ldap.forumsys.com';";

    private static string UpSql() =>
        new DisableSeededForumsysDirectoryLogin().UpOperations.OfType<SqlOperation>().Single().Sql;

    [Test]
    public void Up_IsOneSqlUpdateOfTheProviderTable_AndDeletesNothing()
    {
        var operations = new DisableSeededForumsysDirectoryLogin().UpOperations;
        var sql = UpSql();

        operations.ShouldHaveSingleItem();
        sql.ShouldStartWith("UPDATE identity_providers");
        sql.ShouldNotContain("DELETE", Case.Insensitive);
        sql.ShouldNotContain("is_deleted", Case.Insensitive);
    }

    [Test]
    public void Up_SwitchesOffAuthenticationAndTheEnabledFlag()
    {
        var sql = UpSql();

        sql.ShouldContain("use_for_authentication = false");
        sql.ShouldContain("is_enabled = false");
        sql.ShouldNotContain("true", Case.Insensitive);
    }

    [Test]
    public void Up_IsScopedToTheSeededRowByIdAndByHost()
    {
        var sql = UpSql();

        sql.ShouldContain("id = 'fc0b8e9c-4694-40c1-a10e-fdb66a10b26e'");
        sql.ShouldContain("AND host = 'ldap.forumsys.com'");
        sql.ShouldNotContain("OR ", Case.Insensitive);
    }

    [Test]
    public void Up_IsExactlyTheFrozenStatement()
    {
        Normalize(UpSql()).ShouldBe(FrozenUpSql);
        Normalize(DisableSeededForumsysDirectoryLogin.UpSql).ShouldBe(FrozenUpSql);
    }

    [Test]
    public void Down_IsANoOp()
    {
        new DisableSeededForumsysDirectoryLogin().DownOperations.ShouldBeEmpty();
    }

    [Test]
    public void Seed_InsertsTheForumsysProviderDisabledAndNotUsedForAuthentication()
    {
        var builder = new MigrationBuilder(NpgsqlProvider);

        IdentityProvidersSeed.SeedData(builder);

        var sql = Normalize(Regex.Replace(builder.Operations.OfType<SqlOperation>().Single().Sql, "--[^\\n]*", string.Empty));
        sql.ShouldContain("'Forumsys LDAP Test', 0, false, 10, false, true, 'ldap.forumsys.com'");
    }

    private static string Normalize(string sql) => Regex.Replace(sql, "\\s+", " ").Trim();
}
