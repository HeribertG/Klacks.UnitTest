// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Guard tests for ConditionRemediationRegistry (Etappe 4b): the registry is deliberately empty as of
/// this stage (Owner decision 2026-08-25, see the class's own XML doc), so this pins the invariant that
/// makes that safe rather than merely convenient - a trigger kind with no registered remediation can
/// never be steered past Hint, no matter what agent_trigger_governance configured for it. Exercised
/// against the three real Etappe-2 kinds (the concrete case the 2026-08-25 correction is about) and a
/// synthetic unknown kind (the general case, so the test still means something once a future kind does
/// get an entry).
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
        yield return AgentTriggerKinds.EmptyContainer;
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

    /// <summary>
    /// Pins the current, honest state of the registry itself: every governed kind (the full set
    /// ProactiveGovernanceResolver iterates) is absent. If this ever fails because a kind gained an
    /// entry, that is expected and this test's UnregisteredKinds source (and this assertion) should be
    /// updated deliberately - not a regression to chase blindly.
    /// </summary>
    [Test]
    public void Registry_HasNoEntryForAnyCurrentlyGovernedKind()
    {
        foreach (var kind in ProactiveGovernanceDefaults.GovernedKinds)
        {
            var found = _sut.TryGetEntry(kind, out _);
            Assert.That(found, Is.False, $"Expected no remediation entry for '{kind}' at Etappe 4b.");
        }
    }
}
