// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Guards the goal-type catalogue: every proactive trigger kind that reflection may see must have an
/// entry (otherwise its signals are silently dropped instead of becoming proposals), the i18n keys must
/// be unique per entry, and no text that reaches either the reflection prompt or the planning agent may
/// contain a trigger identifier — that is exactly how identifiers used to leak into user-facing text.
/// </summary>

namespace Klacks.UnitTest.Domain.Constants;

using System.Reflection;
using Klacks.Api.Domain.Constants;

[TestFixture]
public class GoalTypeCatalogTests
{
    // Klacksy's own output; TriggerHistoryGoalSignalSource excludes these from reflection, so they
    // deliberately have no catalogue entry.
    private static readonly string[] ExcludedKinds =
    [
        AgentTriggerKinds.CuriosityQuestion,
        AgentTriggerKinds.MuteSuggestion,
        AgentTriggerKinds.DailyDigest,
        AgentTriggerKinds.ScenarioPrepared
    ];

    private static IEnumerable<string> AllTriggerKinds() =>
        typeof(AgentTriggerKinds)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!);

    [Test]
    public void EveryReflectableTriggerKind_HasACatalogueEntry()
    {
        var missing = AllTriggerKinds()
            .Where(kind => !ExcludedKinds.Contains(kind))
            .Where(kind => GoalTypeCatalog.Find(kind) == null)
            .ToList();

        missing.ShouldBeEmpty(
            "Every trigger kind reflection can observe needs a GoalTypeCatalog entry with its i18n keys, " +
            "otherwise its signals are dropped instead of becoming a proposal: " + string.Join(", ", missing));
    }

    [Test]
    public void ExcludedTriggerKinds_HaveNoCatalogueEntry()
    {
        foreach (var kind in ExcludedKinds)
        {
            GoalTypeCatalog.Find(kind).ShouldBeNull($"'{kind}' is Klacksy's own output and must not be proposable.");
        }
    }

    [Test]
    public void EveryEntry_IsKeyedByItsOwnTriggerKind()
    {
        foreach (var (kind, definition) in GoalTypeCatalog.ByTriggerKind)
        {
            definition.TriggerKind.ShouldBe(kind);
        }
    }

    [Test]
    public void I18nKeys_AreUniqueAcrossEntries()
    {
        var keys = GoalTypeCatalog.ByTriggerKind.Values
            .SelectMany(d => new[] { d.TitleKey, d.RationaleKey })
            .ToList();

        keys.Distinct().Count().ShouldBe(keys.Count);
    }

    [Test]
    public void NoDisplayableText_ContainsATriggerIdentifier()
    {
        var identifiers = AllTriggerKinds().ToList();

        foreach (var definition in GoalTypeCatalog.ByTriggerKind.Values)
        {
            foreach (var text in new[] { definition.SignalDescription, definition.PlannerTitle, definition.PlannerRationaleFormat })
            {
                identifiers.Any(text.Contains).ShouldBeFalse(
                    $"'{text}' contains a trigger identifier; catalogue texts are read by the reflection model " +
                    "and the planning agent and must stay free of internal names.");
            }
        }
    }

    [Test]
    public void EveryRationaleFormat_UsesBothPlaceholders()
    {
        foreach (var definition in GoalTypeCatalog.ByTriggerKind.Values)
        {
            definition.PlannerRationaleFormat.ShouldContain("{0}");
            definition.PlannerRationaleFormat.ShouldContain("{1}");
        }
    }
}
