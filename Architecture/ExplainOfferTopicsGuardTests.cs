// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Guards the explain-offer topic registry (ExplainOfferTopics) against the rest of the system: every topic's happen and every
/// gate skill is a real seeded skill (a rename would silently switch an offer off), the happen is a knowledge-happen, a gate
/// belongs to exactly one topic, the trigger kinds fit their column and stay unique, the preview gates are catalogued preview
/// skills, the mirrored skill-name constants equal the skill classes' own, and beginners are never offered the advanced-only
/// planning topics.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class ExplainOfferTopicsGuardTests
{
    private const int TriggerKindMaxLength = 64;
    private const string SkillSeedsPath = "Application/Skills/Definitions/skill-seeds.json";
    private const string SkillsProperty = "skills";
    private const string NameProperty = "name";
    private const string HandlerTypeProperty = "handlerType";
    private const string KnowledgeHappenHandler = "knowledge-happen";

    private static IReadOnlyDictionary<string, string?> SeededSkills()
    {
        var path = Path.Combine(RepositoryRootLocator.ApiProject, SkillSeedsPath);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty(SkillsProperty).EnumerateArray()
            .ToDictionary(
                skill => skill.GetProperty(NameProperty).GetString()!,
                skill => skill.TryGetProperty(HandlerTypeProperty, out var handler) ? handler.GetString() : null,
                StringComparer.Ordinal);
    }

    private static IEnumerable<string> GateSkills() =>
        ExplainOfferTopics.All.SelectMany(topic => topic.GateSkills.Concat(topic.PreviewGateSkills));

    [Test]
    public void EveryTopicsHappen_IsASeededKnowledgeHappen()
    {
        var seeded = SeededSkills();

        foreach (var topic in ExplainOfferTopics.All)
        {
            seeded.ShouldContainKey(topic.HappenSkillName, $"{topic.Key}: happen '{topic.HappenSkillName}' is not seeded");
            seeded[topic.HappenSkillName].ShouldBe(KnowledgeHappenHandler, $"{topic.Key}: '{topic.HappenSkillName}' is no knowledge happen");
        }
    }

    [Test]
    public void EveryGateSkill_IsASeededSkill()
    {
        var seeded = SeededSkills();

        foreach (var gate in GateSkills())
        {
            seeded.ShouldContainKey(gate, $"gate skill '{gate}' is not seeded");
        }
    }

    [Test]
    public void AGateSkill_BelongsToExactlyOneTopic()
    {
        var gates = GateSkills().ToList();

        gates.Distinct(StringComparer.OrdinalIgnoreCase).Count().ShouldBe(gates.Count);
        gates.ShouldNotBeEmpty();
    }

    [Test]
    public void EveryTopic_HasAtLeastOneGate_AndAUniqueKeyAndAFittingTriggerKind()
    {
        ExplainOfferTopics.All.Select(topic => topic.Key).Distinct(StringComparer.Ordinal).Count()
            .ShouldBe(ExplainOfferTopics.All.Count);

        foreach (var topic in ExplainOfferTopics.All)
        {
            (topic.GateSkills.Count + topic.PreviewGateSkills.Count).ShouldBeGreaterThan(0, topic.Key);
            var kind = ExplainOfferTopics.TriggerKindOf(topic);
            kind.Length.ShouldBeLessThanOrEqualTo(TriggerKindMaxLength, kind);
            ExplainOfferTopics.IsOfferKind(kind).ShouldBeTrue(kind);
            ExplainOfferTopics.FindByTriggerKind(kind).ShouldBeSameAs(topic);
        }
    }

    [Test]
    public void EveryPreviewGate_IsACataloguedPreviewApplySkill()
    {
        foreach (var gate in ExplainOfferTopics.All.SelectMany(topic => topic.PreviewGateSkills))
        {
            PreviewApplySkillCalls.Skills.ShouldContain(gate);
        }
    }

    [Test]
    public void TheMirroredSkillNameConstants_EqualTheSkillClassesOwn()
    {
        ExplainOfferTopics.BulkDeleteWorksSkillName.ShouldBe(BulkDeleteWorksSkill.SkillName);
        ExplainOfferTopics.FindByConfirmationGate(ScenarioAcceptDefaults.SkillName)!.Key
            .ShouldBe(ExplainOfferTopics.ScenarioAcceptKey);
        ExplainOfferTopics.FindByConfirmationGate(PlanSkillDefaults.CreatePlanSkillName)!.Key
            .ShouldBe(ExplainOfferTopics.PlanKey);
    }

    [Test]
    public void OnlyThePlanningTopics_AreAdvancedOnly()
    {
        ExplainOfferTopics.All
            .Where(topic => topic.Audience == ExplainOfferAudience.AdvancedOnly)
            .Select(topic => topic.Key)
            .ShouldBe([ExplainOfferTopics.PlanKey, ExplainOfferTopics.PlanningCommandsKey], ignoreOrder: true);
    }

    [Test]
    public void TheLookups_AnswerNullForForeignNames()
    {
        ExplainOfferTopics.FindByConfirmationGate("delete_system_user").ShouldBeNull();
        ExplainOfferTopics.FindByConfirmationGate(null).ShouldBeNull();
        ExplainOfferTopics.FindByPreviewGate(ExplainOfferTopics.BulkDeleteWorksSkillName).ShouldBeNull();
        ExplainOfferTopics.FindByConfirmationGate(ExplainOfferTopics.SealOpenOrdersSkillName).ShouldBeNull();
        ExplainOfferTopics.FindByTriggerKind("scenario_prepared").ShouldBeNull();
        ExplainOfferTopics.IsOfferKind("scenario_prepared").ShouldBeFalse();
        ExplainOfferTopics.IsOfferKind(null).ShouldBeFalse();
    }

    [Test]
    public void OnlyHeldGateTopicsRenew_AndOnlyTheScenarioTopicRenewsAStaleOverwrite()
    {
        foreach (var topic in ExplainOfferTopics.All)
        {
            var purposes = ExplainOfferTopics.RenewablePurposesOf(topic);
            if (topic.GateSkills.Count == 0)
            {
                purposes.ShouldBeEmpty(topic.Key);
                continue;
            }

            purposes.ShouldContain(PendingConfirmationPurposes.GateReplay, topic.Key);
            purposes.Contains(PendingConfirmationPurposes.StaleScenarioOverwrite)
                .ShouldBe(topic.Key == ExplainOfferTopics.ScenarioAcceptKey, topic.Key);
            purposes.ShouldNotContain(PendingConfirmationPurposes.CorrectionUndo, topic.Key);
            purposes.ShouldNotContain(PendingConfirmationPurposes.ProposalHint, topic.Key);
        }
    }
}
