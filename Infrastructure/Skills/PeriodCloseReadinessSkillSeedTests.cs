// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the seeded rights and shape of evaluate_period_close_readiness: the same right as the findings list and the
/// close it advises on, a read-only Query with the Advise effect in the period area, the AdvisesFor edge to close_period,
/// and a description that never names its close neighbours (see skill-discoverability.md section 4).
/// </summary>

using System.Text.Json;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Infrastructure.Skills;

[TestFixture]
public class PeriodCloseReadinessSkillSeedTests
{
    private const string AdviceSkill = "evaluate_period_close_readiness";
    private const string IssuesSkill = "list_period_issues";
    private const string CloseSkill = "close_period";
    private const int AdvisesForType = 3;

    private static readonly string[] DefinitionsRelativePath =
    [
        "Klacks.Api", "Application", "Skills", "Definitions"
    ];

    private static readonly string[] Neighbours = [IssuesSkill, CloseSkill, "get_period_status", "get_period_close_schedule"];

    [Test]
    public void AdviceSkill_AsksForTheSameRightAsTheFindingListAndTheClose()
    {
        var advice = Rights(AdviceSkill);

        advice.ShouldBe(Rights(IssuesSkill));
        advice.ShouldBe(Rights(CloseSkill));
    }

    [Test]
    public void AdviceSkill_IsAReadOnlyQueryWithTheAdviseEffectInThePeriodArea()
    {
        var skill = Seeded(AdviceSkill);

        skill.GetProperty("category").GetString().ShouldBe("Query");
        skill.GetProperty("effect").GetString().ShouldBe("Advise");
        skill.GetProperty("area").GetString().ShouldBe("period");
        skill.GetProperty("alwaysOn").GetBoolean().ShouldBeFalse();
    }

    [Test]
    public void AdviceSkill_HasAnAdvisesForEdgeToTheClose()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Locate("skill-relation-seeds.json")));

        document.RootElement.GetProperty("relations").EnumerateArray().ShouldContain(relation =>
            relation.GetProperty("skillAName").GetString() == AdviceSkill &&
            relation.GetProperty("skillBName").GetString() == CloseSkill &&
            relation.GetProperty("type").GetInt32() == AdvisesForType);
    }

    [Test]
    public void AdviceDescription_NeverNamesItsNeighbours()
    {
        var description = Seeded(AdviceSkill).GetProperty("description").GetString()!;

        foreach (var neighbour in Neighbours)
        {
            description.ShouldNotContain(neighbour);
        }
    }

    private static readonly string[] AdviceOpeners =
    [
        "kann ich", "darf ich", "können wir", "koennen wir", "can i", "can we", "may i", "am i allowed",
        "puis-je", "peut-on", "posso", "possiamo"
    ];

    [Test]
    public void FindingList_CarriesNoGoOrNoGoQuestion_ThatBelongsToTheAdviceSkill()
    {
        var phrases = Synonyms(IssuesSkill);

        phrases.ShouldNotBeEmpty();
        foreach (var phrase in phrases)
        {
            AdviceOpeners.Any(opener => phrase.StartsWith(opener, StringComparison.OrdinalIgnoreCase))
                .ShouldBeFalse($"'{phrase}' is a go or no-go question and belongs to {AdviceSkill}, not to {IssuesSkill}.");
        }
    }

    [Test]
    public void FindingList_StillAnswersTheListingQuestions()
    {
        var phrases = Synonyms(IssuesSkill);

        phrases.ShouldContain("probleme der periode anzeigen");
        phrases.ShouldContain("show period issues");
        phrases.ShouldContain("which problems block the period closing");
    }

    private static List<string> Synonyms(string skillName) =>
        Seeded(skillName).GetProperty("synonyms").EnumerateObject()
            .SelectMany(language => language.Value.EnumerateArray().Select(phrase => phrase.GetString()!))
            .ToList();

    private static List<string?> Rights(string skillName) =>
        Seeded(skillName).GetProperty("requiredPermissions").EnumerateArray().Select(p => p.GetString()).ToList();

    private static JsonElement Seeded(string skillName)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Locate("skill-seeds.json")));

        var skill = document.RootElement.GetProperty("skills")
            .EnumerateArray()
            .FirstOrDefault(s => s.TryGetProperty("name", out var n) && n.GetString() == skillName);

        skill.ValueKind.ShouldNotBe(JsonValueKind.Undefined, $"'{skillName}' is missing from the seed file.");
        return skill.Clone();
    }

    private static string Locate(string fileName) =>
        RepositoryRootLocator.RequireFile([.. DefinitionsRelativePath, fileName]);
}
