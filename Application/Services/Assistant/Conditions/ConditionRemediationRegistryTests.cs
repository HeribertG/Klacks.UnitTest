// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Guard tests for ConditionRemediationRegistry. The invariant they pin is what makes the registry a
/// security gate rather than a lookup table: a trigger kind with no registered remediation can never be
/// steered past Hint, no matter what agent_trigger_governance or an Etappe-4e delegation configured for
/// it, because nothing exists that could carry the remediation out.
///
/// Etappe 5b added the first entry (empty_container). The kinds below are the ones that are still
/// deliberately absent - open_order and uncut_fullday_shift, whose remediations were never built - plus
/// a synthetic unknown kind, so the general case keeps being covered as further kinds gain entries.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Conditions;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;

namespace Klacks.UnitTest.Application.Services.Assistant.Conditions;

[TestFixture]
public class ConditionRemediationRegistryTests
{
    private const string SyntheticUnregisteredKind = "synthetic_test_kind_never_registered";

    private ConditionRemediationRegistry _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _sut = new ConditionRemediationRegistry();
    }

    private static IEnumerable<string> UnregisteredKinds()
    {
        yield return AgentTriggerKinds.OpenOrder;
        yield return AgentTriggerKinds.UncutFulldayShift;
        yield return SyntheticUnregisteredKind;
    }

    [TestCaseSource(nameof(UnregisteredKinds))]
    public void TryGetEntry_KindWithoutARegisteredRemediation_ReturnsFalse(string triggerKind)
    {
        var found = _sut.TryGetEntry(triggerKind, out var entry);

        Assert.That(found, Is.False);
        Assert.That(entry, Is.Null);
    }

    [TestCaseSource(nameof(UnregisteredKinds))]
    public void TryGetEffectiveMaxAction_ConfiguredPrepare_CapsAtHintForAnUnregisteredKind(string triggerKind)
    {
        var effective = _sut.TryGetEffectiveMaxAction(triggerKind, ProactiveMaxAction.Prepare);

        Assert.That(effective, Is.EqualTo(ProactiveMaxAction.Hint));
    }

    [TestCaseSource(nameof(UnregisteredKinds))]
    public void TryGetEffectiveMaxAction_ConfiguredExecute_CapsAtHintForAnUnregisteredKind(string triggerKind)
    {
        var effective = _sut.TryGetEffectiveMaxAction(triggerKind, ProactiveMaxAction.Execute);

        Assert.That(effective, Is.EqualTo(ProactiveMaxAction.Hint));
    }

    [TestCaseSource(nameof(UnregisteredKinds))]
    public void TryGetEffectiveMaxAction_ConfiguredHint_StaysHint(string triggerKind)
    {
        var effective = _sut.TryGetEffectiveMaxAction(triggerKind, ProactiveMaxAction.Hint);

        Assert.That(effective, Is.EqualTo(ProactiveMaxAction.Hint));
    }

    [Test]
    public void EmptyContainer_IsRemediatedByCreateContainerTemplateAndIsExecuteOnly()
    {
        var found = _sut.TryGetEntry(AgentTriggerKinds.EmptyContainer, out var entry);

        Assert.That(found, Is.True);
        Assert.That(entry!.RemediationSkillName, Is.EqualTo(CreateContainerTemplateParameters.SkillName));
        Assert.That(
            entry.IsScenarioCapable,
            Is.False,
            "Creating a container template is a structural Shift change; AcceptAnalyseScenarioCommandHandler "
            + "only ever promotes Work, WorkChange and Expenses rows out of a scenario, so staging one "
            + "would leave an AnalyseScenario nobody can accept.");
        Assert.That(entry.RequiredArguments, Is.EquivalentTo(CreateContainerTemplateParameters.Required));
    }

    [Test]
    public void EmptyContainer_ConfiguredExecute_KeepsExecute()
    {
        var effective = _sut.TryGetEffectiveMaxAction(AgentTriggerKinds.EmptyContainer, ProactiveMaxAction.Execute);

        Assert.That(effective, Is.EqualTo(ProactiveMaxAction.Execute));
    }

    [Test]
    public void RegisteredKinds_AreAllGovernedKinds()
    {
        foreach (var kind in _sut.RegisteredKinds)
        {
            Assert.That(
                ProactiveGovernanceDefaults.IsGovernedKind(kind),
                Is.True,
                $"'{kind}' has a remediation but no governance rule can address it, so nothing could ever "
                + "raise it past the fail-safe Hint default - the entry would be unreachable.");
        }
    }
}
