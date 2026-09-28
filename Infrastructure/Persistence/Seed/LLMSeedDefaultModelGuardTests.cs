// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Guards that a fresh installation's LLM model catalog carries exactly one default model, that it is
/// deepseek-flash, and that it is enabled. LLMRepository.GetDefaultModelAsync orders by model_id and
/// returns the first row with is_default = true, so more than one default row on a fresh install makes
/// the effective default depend on alphabetical ordering rather than an explicit owner decision.
/// </summary>

using System.Text.RegularExpressions;
using Klacks.Api.Data.Seed;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace Klacks.UnitTest.Infrastructure.Persistence.Seed;

[TestFixture]
public class LLMSeedDefaultModelGuardTests
{
    private const string ExpectedDefaultModelId = "deepseek-flash";

    private static readonly Regex ModelRow = new(
        @"(?:\(|SELECT\s+)gen_random_uuid\(\),\s*'(?<modelId>[^']+)',\s*'[^']*',\s*'[^']*',\s*'[^']*',\s*(?<enabled>true|false),\s*(?<default>true|false)",
        RegexOptions.Compiled);

    private sealed record ModelRowFlags(string ModelId, bool IsEnabled, bool IsDefault);

    private static List<ModelRowFlags> SeedModelRows()
    {
        var builder = new MigrationBuilder(null);
        LLMSeed.SeedData(builder);

        var sql = string.Join(
            Environment.NewLine,
            builder.Operations.OfType<SqlOperation>().Select(o => o.Sql));

        return ModelRow.Matches(sql)
            .Select(m => new ModelRowFlags(
                m.Groups["modelId"].Value,
                bool.Parse(m.Groups["enabled"].Value),
                bool.Parse(m.Groups["default"].Value)))
            .ToList();
    }

    [Test]
    public void SeedModelRows_ParsesAtLeastTheKnownCatalogSize()
    {
        SeedModelRows().Count.ShouldBeGreaterThanOrEqualTo(30);
    }

    [Test]
    public void FreshInstall_HasExactlyOneDefaultModel()
    {
        var defaults = SeedModelRows().Where(r => r.IsDefault).ToList();

        defaults.Count.ShouldBe(1);
    }

    [Test]
    public void FreshInstall_DefaultModelIsDeepseekFlash()
    {
        var defaults = SeedModelRows().Where(r => r.IsDefault).ToList();

        defaults.ShouldHaveSingleItem();
        defaults.Single().ModelId.ShouldBe(ExpectedDefaultModelId);
    }

    [Test]
    public void FreshInstall_DefaultModelIsEnabled()
    {
        var defaultRow = SeedModelRows().Single(r => r.IsDefault);

        defaultRow.IsEnabled.ShouldBeTrue();
    }

    [Test]
    public void FreshInstall_GptAndGeminiFlagshipsAreNoLongerDefault()
    {
        var rows = SeedModelRows().ToDictionary(r => r.ModelId);

        rows["gpt-54"].IsDefault.ShouldBeFalse();
        rows["gemini-31-pro"].IsDefault.ShouldBeFalse();
    }
}
