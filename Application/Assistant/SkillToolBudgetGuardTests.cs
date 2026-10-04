// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Guard against the tool-budget drift that broke chat skill routing: when the number of alwaysOn
/// skills grows up to the MaxToolsForProvider cap, the truncation (which orders alwaysOn first) squeezes
/// out every retrieved (non-alwaysOn) skill, so no retrieved skill ever reaches the LLM. This asserts
/// the invariant alwaysOn + DefaultTopK <= MaxToolsForProvider so the regression surfaces as a red test
/// instead of a silent production failure (a chat that can only use alwaysOn skills).
/// </summary>

using System.Text.Json;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.KnowledgeIndex.Application.Constants;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Application.Assistant;

[TestFixture]
public class SkillToolBudgetGuardTests
{
    [Test]
    public void AlwaysOnSkillsPlusTopK_FitWithinToolBudget()
    {
        var seedPath = LocateSkillSeeds();
        using var doc = JsonDocument.Parse(File.ReadAllText(seedPath));

        var alwaysOn = doc.RootElement.GetProperty("skills").EnumerateArray()
            .Count(s => s.TryGetProperty("alwaysOn", out var v) && v.ValueKind == JsonValueKind.True);

        (alwaysOn + KnowledgeIndexConstants.DefaultTopK)
            .ShouldBeLessThanOrEqualTo(
                KnowledgeIndexConstants.MaxToolsForProvider,
                $"alwaysOn skills ({alwaysOn}) + DefaultTopK ({KnowledgeIndexConstants.DefaultTopK}) exceed " +
                $"MaxToolsForProvider ({KnowledgeIndexConstants.MaxToolsForProvider}); retrieved skills would be " +
                "truncated away and become unreachable via chat. Raise the cap or reduce alwaysOn skills.");
    }

    [Test]
    public void AlwaysOnSkillCount_FitsWithinAdaptiveMinToolsForProvider()
    {
        var seedPath = LocateSkillSeeds();
        using var doc = JsonDocument.Parse(File.ReadAllText(seedPath));

        var alwaysOn = doc.RootElement.GetProperty("skills").EnumerateArray()
            .Count(s => s.TryGetProperty("alwaysOn", out var v) && v.ValueKind == JsonValueKind.True);

        alwaysOn.ShouldBeLessThan(
            ContextBudgetPolicy.MinToolsForProvider,
            $"alwaysOn skill count ({alwaysOn}) leaves no headroom under MinToolsForProvider " +
            $"({ContextBudgetPolicy.MinToolsForProvider}); the tightest adaptive tool-budget tier " +
            "(small-context models) would squeeze out every deterministically guaranteed skill " +
            "(page-explain, recipe steps, ...). Raise MinToolsForProvider or reduce alwaysOn skills.");
    }

    private const int MaxAlwaysOnSkills = 10;

    [Test]
    public void AlwaysOnSkillCount_DoesNotRegrowUnchecked()
    {
        var seedPath = LocateSkillSeeds();
        using var doc = JsonDocument.Parse(File.ReadAllText(seedPath));

        var alwaysOn = doc.RootElement.GetProperty("skills").EnumerateArray()
            .Count(s => s.TryGetProperty("alwaysOn", out var v) && v.ValueKind == JsonValueKind.True);

        alwaysOn.ShouldBeLessThanOrEqualTo(
            MaxAlwaysOnSkills,
            $"alwaysOn skill count ({alwaysOn}) exceeds {MaxAlwaysOnSkills}; new skills should default to " +
            "retrieval-only unless there is a specific reason every chat turn must see them.");
    }

    private static string LocateSkillSeeds()
    {
        return RepositoryRootLocator.RequireFile(["Klacks.Api", "Application", "Skills", "Definitions", "skill-seeds.json"]);
    }
}
