// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Guard against the tool-budget drift that broke chat skill routing: when the number of alwaysOn
/// skills grows up to the MaxToolsForProvider cap, the truncation (which orders alwaysOn first) squeezes
/// out every retrieved (non-alwaysOn) skill, so no retrieved skill ever reaches the LLM. This asserts
/// the invariant alwaysOn + DefaultTopK <= MaxToolsForProvider so the regression surfaces as a red test
/// instead of a silent production failure (a chat that can only use alwaysOn skills).
/// </summary>

using System.Text.Json;
using Klacks.Api.KnowledgeIndex.Application.Constants;

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
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "Klacks.Api", "Application", "Skills", "Definitions", "skill-seeds.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }
            dir = dir.Parent;
        }
        throw new FileNotFoundException("Could not locate Klacks.Api/Application/Skills/Definitions/skill-seeds.json by walking up from the test base directory.");
    }
}
