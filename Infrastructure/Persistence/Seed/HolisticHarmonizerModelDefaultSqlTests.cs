// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Guards the Holistic Harmonizer (Wizard 3) model default: gemini-25-flash is installed only where no
/// model was chosen yet and the model can actually be called (enabled model, enabled provider with a key),
/// and the former demo choice gemini-35-flash - a thinking model whose pre-flight ping failed - is replaced
/// only when it is still exactly that value. Every other customer choice stays untouched. The default
/// must exist as an enabled Google model in the fresh-install LLM catalog.
/// </summary>

using System.Text.RegularExpressions;
using AppSettings = Klacks.Api.Application.Constants.Settings;
using Klacks.Api.Data.Seed;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace Klacks.UnitTest.Infrastructure.Persistence.Seed;

[TestFixture]
public class HolisticHarmonizerModelDefaultSqlTests
{
    private static List<string> ApplyStatements()
    {
        var builder = new MigrationBuilder(activeProvider: null);
        HolisticHarmonizerModelDefaultSql.Apply(builder);
        return builder.Operations.OfType<SqlOperation>().Select(o => Normalize(o.Sql)).ToList();
    }

    private static string Normalize(string sql) => Regex.Replace(sql, @"\s+", " ").Trim();

    [Test]
    public void SettingType_MatchesTheApplicationConstant()
    {
        HolisticHarmonizerModelDefaultSql.SettingType.ShouldBe(AppSettings.HOLISTIC_HARMONIZER_LLM_MODEL);
    }

    [Test]
    public void DefaultModel_IsGemini25Flash()
    {
        HolisticHarmonizerModelDefaultSql.DefaultModelId.ShouldBe("gemini-25-flash");
        HolisticHarmonizerModelDefaultSql.LegacyDefaultModelId.ShouldBe("gemini-35-flash");
    }

    [Test]
    public void Apply_EmitsOneInsertAndOneUpdate()
    {
        var statements = ApplyStatements();

        statements.Count.ShouldBe(2);
        statements.Count(s => s.StartsWith("INSERT INTO settings", StringComparison.Ordinal)).ShouldBe(1);
        statements.Count(s => s.StartsWith("UPDATE settings", StringComparison.Ordinal)).ShouldBe(1);
    }

    [Test]
    public void Insert_OnlyWhenNoModelIsConfiguredYet()
    {
        var insert = ApplyStatements().Single(s => s.StartsWith("INSERT", StringComparison.Ordinal));

        insert.ShouldContain("'WIZARD3_LLM_MODEL', 'gemini-25-flash'");
        insert.ShouldContain("WHERE NOT EXISTS (SELECT 1 FROM settings WHERE type = 'WIZARD3_LLM_MODEL')");
    }

    [Test]
    public void Insert_OnlyWhenTheDefaultModelIsCallable()
    {
        var insert = ApplyStatements().Single(s => s.StartsWith("INSERT", StringComparison.Ordinal));

        insert.ShouldContain("m.model_id = 'gemini-25-flash'");
        insert.ShouldContain("m.is_enabled");
        insert.ShouldContain("NOT m.is_deleted");
        insert.ShouldContain("p.is_enabled");
        insert.ShouldContain("NOT p.is_deleted");
        insert.ShouldContain("COALESCE(p.api_key, '') <> ''");
    }

    [Test]
    public void Update_ReplacesOnlyTheExactLegacyValue()
    {
        var update = ApplyStatements().Single(s => s.StartsWith("UPDATE", StringComparison.Ordinal));

        update.ShouldContain("SET value = 'gemini-25-flash'");
        update.ShouldContain("WHERE type = 'WIZARD3_LLM_MODEL' AND value = 'gemini-35-flash'");
        update.ShouldNotContain("ILIKE");
        update.ShouldNotContain("LIKE '");
    }

    [Test]
    public void Update_OnlyWhenTheDefaultModelIsEnabled()
    {
        var update = ApplyStatements().Single(s => s.StartsWith("UPDATE", StringComparison.Ordinal));

        update.ShouldContain("m.model_id = 'gemini-25-flash'");
        update.ShouldContain("m.is_enabled");
        update.ShouldContain("NOT m.is_deleted");
    }

    [Test]
    public void DefaultModel_IsAnEnabledGoogleModelOfTheFreshInstallCatalog()
    {
        var builder = new MigrationBuilder(activeProvider: null);
        LLMSeed.SeedData(builder);
        var seedSql = Normalize(string.Join(" ", builder.Operations.OfType<SqlOperation>().Select(o => o.Sql)));

        seedSql.ShouldContain($"'{HolisticHarmonizerModelDefaultSql.DefaultModelId}', 'Gemini 2.5 Flash', 'gemini-2.5-flash', 'google', true,");
    }
}
