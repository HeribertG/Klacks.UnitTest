// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for the BackfillSeededQwenCompatibleBaseUrl migration: a pure data fix (one UPDATE, no schema change)
/// that moves the seeded Qwen provider from the native DashScope API to the OpenAI-compatible endpoint, but only
/// while the row still holds exactly the old seeded URL. The current LLMSeed must write the corrected URL, so a fresh
/// install and a repaired database end up identical, and the URL must end with a slash because the provider resolves
/// the relative "chat/completions" path against it.
/// </summary>

using System.Text.RegularExpressions;
using Klacks.Api.Data.Seed;
using Klacks.Api.Infrastructure.Persistence.Migrations;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace Klacks.UnitTest.Infrastructure.Persistence.Migrations;

[TestFixture]
public class BackfillSeededQwenCompatibleBaseUrlMigrationTests
{
    private const string ChatCompletionsPath = "chat/completions";
    private const string ExpectedChatCompletionsUrl = "https://dashscope-intl.aliyuncs.com/compatible-mode/v1/chat/completions";

    private static readonly Regex QwenSeedRow = new(
        "'qwen', '[^']*', (?:true|false), \\d+, '(?<url>[^']*)'",
        RegexOptions.Compiled);

    private static IReadOnlyList<MigrationOperation> Up() => new BackfillSeededQwenCompatibleBaseUrl().UpOperations;

    [Test]
    public void IsOneDataStatement_WithoutSchemaChanges()
    {
        var up = Up();

        up.Count.ShouldBe(1);
        up.ShouldAllBe(op => op is SqlOperation);
    }

    [Test]
    public void Down_IsEmpty_BecauseItWouldReintroduceTheBrokenUrl()
    {
        new BackfillSeededQwenCompatibleBaseUrl().DownOperations.ShouldBeEmpty();
    }

    [Test]
    public void TheStatement_UpdatesOnlyTheQwenRowThatStillHoldsTheExactOldSeededUrl()
    {
        var sql = Up().OfType<SqlOperation>().Single().Sql;

        sql.ShouldBe(LLMProviderBaseUrlCorrectionSql.BuildQwenStatement());
        sql.ShouldStartWith("UPDATE llm_providers SET base_url = 'https://dashscope-intl.aliyuncs.com/compatible-mode/v1/'");
        sql.ShouldContain("WHERE provider_id = 'qwen' AND base_url = 'https://dashscope.aliyuncs.com/api/v1/';");
        sql.ShouldNotContain("LIKE", Case.Insensitive);
        sql.ShouldNotContain(" OR ", Case.Insensitive);
    }

    [Test]
    public void TheFrozenValues_AreTheOldSeedAndAnOpenAICompatibleEndpoint()
    {
        LLMProviderBaseUrlCorrectionSql.FaultyQwenBaseUrl.ShouldBe("https://dashscope.aliyuncs.com/api/v1/");
        LLMProviderBaseUrlCorrectionSql.CorrectedQwenBaseUrl.ShouldContain("/compatible-mode/v1/");
        LLMProviderBaseUrlCorrectionSql.CorrectedQwenBaseUrl.ShouldNotBe(LLMProviderBaseUrlCorrectionSql.FaultyQwenBaseUrl);
    }

    [Test]
    public void TheCurrentSeed_WritesTheCorrectedUrl()
    {
        SeededQwenBaseUrl().ShouldBe(LLMProviderBaseUrlCorrectionSql.CorrectedQwenBaseUrl);
    }

    [Test]
    public void TheCorrectedUrl_ResolvesChatCompletionsBelowTheCompatibleModePath()
    {
        var resolved = new Uri(new Uri(LLMProviderBaseUrlCorrectionSql.CorrectedQwenBaseUrl), ChatCompletionsPath);

        resolved.ToString().ShouldBe(ExpectedChatCompletionsUrl);
    }

    private static string SeededQwenBaseUrl()
    {
        var builder = new MigrationBuilder(activeProvider: null);
        LLMSeed.SeedData(builder);
        var sql = string.Concat(builder.Operations.OfType<SqlOperation>().Select(o => o.Sql));

        var matches = QwenSeedRow.Matches(sql);
        matches.Count.ShouldBe(1);
        return matches[0].Groups["url"].Value;
    }
}
