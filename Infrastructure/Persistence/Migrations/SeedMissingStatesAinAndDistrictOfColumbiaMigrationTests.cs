// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for the SeedMissingStatesAinAndDistrictOfColumbia migration: one guarded INSERT per missing state
/// (no schema change), with the same id, code and country prefix DefaultSeed uses, inserted only on a database
/// whose states of that country are already seeded and where the code is still missing - so a fresh install,
/// where the migrations run before DefaultSeed, does not collide with the seed's own rows.
/// </summary>

using Klacks.Api.Infrastructure.Persistence.Migrations;
using Klacks.UnitTest.TestHelpers;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace Klacks.UnitTest.Infrastructure.Persistence.Migrations;

[TestFixture]
public class SeedMissingStatesAinAndDistrictOfColumbiaMigrationTests
{
    private static readonly string[] DefaultSeedRelativePath =
        ["Klacks.Api", "Infrastructure", "Persistence", "Seed", "DefaultSeed.cs"];

    private static readonly (string Id, string Code, string Prefix)[] ExpectedRows =
    [
        ("d6ae2a47-8e65-4ccd-bbe9-000000000001", "01", "FR"),
        ("cf9361fb-5792-4d93-9ad5-000000000061", "DC", "USA")
    ];

    [Test]
    public void Up_IsOneGuardedInsertPerState_WithoutSchemaChanges()
    {
        var up = new SeedMissingStatesAinAndDistrictOfColumbia().UpOperations;

        up.Count.ShouldBe(ExpectedRows.Length);
        up.ShouldAllBe(op => op is SqlOperation);
        foreach (var (operation, (id, code, prefix)) in up.Cast<SqlOperation>().Zip(ExpectedRows))
        {
            operation.Sql.ShouldContain($"SELECT '{id}', '{code}', '{prefix}'");
            operation.Sql.ShouldContain($"WHERE EXISTS (SELECT 1 FROM public.state WHERE country_prefix = '{prefix}')");
            operation.Sql.ShouldContain($"NOT EXISTS (SELECT 1 FROM public.state WHERE country_prefix = '{prefix}' AND abbreviation = '{code}')");
            operation.Sql.ShouldContain("ON CONFLICT (id) DO NOTHING");
        }
    }

    [Test]
    public void EveryInsertedRow_IsAlsoInDefaultSeed_WithTheSameIdCodeAndPrefix()
    {
        var seed = File.ReadAllText(RepositoryRootLocator.RequireFile(DefaultSeedRelativePath));

        foreach (var (id, code, prefix) in ExpectedRows)
        {
            seed.ShouldContain($"('{id}', '{code}', '{prefix}', ");
        }
    }

    [Test]
    public void Down_RemovesExactlyTheTwoRows()
    {
        var down = new SeedMissingStatesAinAndDistrictOfColumbia().DownOperations;

        var sql = down.Cast<SqlOperation>().Single().Sql;
        sql.ShouldStartWith("DELETE FROM public.state WHERE id IN (");
        foreach (var (id, _, _) in ExpectedRows)
        {
            sql.ShouldContain(id);
        }
    }
}
