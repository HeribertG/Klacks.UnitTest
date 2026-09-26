// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Guards the boundaries the autonomous period close was built inside. The only unattended way to seal a
/// period is PeriodAutoCloseService; these tests fail when somebody opens a second one: close_period loses
/// its Sensitive class (the chat gate, plans and UnattendedSkillPolicy would then let it through), the
/// condition remediation registry learns to run close_period for a finding, or the period_auto_close rule is
/// seeded at a ceiling above Hint so installations would start opted in. The classification is read back
/// from the production classifier, not restated.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Conditions;
using Klacks.Api.Application.Skills.Meta;
using Klacks.Api.Domain.Constants;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class PeriodAutoCloseSafetyGuardTests
{
    private const string ClosePeriodSkill = "close_period";

    [Test]
    public void ClosePeriod_StaysSensitive()
    {
        var descriptor = new SkillDescriptor(ClosePeriodSkill, "test", SkillCategory.Crud, [], [], [], null);

        new SkillRiskClassifier().Classify(descriptor).ShouldBe(SkillRiskClass.Sensitive);
    }

    [Test]
    public void NoConditionRemediationRunsClosePeriod()
    {
        var registry = new ConditionRemediationRegistry();

        foreach (var kind in registry.RegisteredKinds)
        {
            registry.TryGetEntry(kind, out var entry).ShouldBeTrue();
            entry!.RemediationSkillName.ShouldNotBe(ClosePeriodSkill, $"Kind {kind} remediates by closing a period.");
        }

        registry.TryGetEntry(AgentTriggerKinds.PeriodAutoClose, out _).ShouldBeFalse();
        registry.TryGetEntry(AgentTriggerKinds.PeriodCloseDue, out _).ShouldBeFalse();
        registry.TryGetEntry(AgentTriggerKinds.PeriodOverdue, out _).ShouldBeFalse();
    }

    [Test]
    public void PeriodAutoCloseRule_IsGovernedAndSeededAtTheFailSafeHint()
    {
        ProactiveGovernanceDefaults.IsGovernedKind(AgentTriggerKinds.PeriodAutoClose).ShouldBeTrue();
        ProactiveGovernanceDefaults.SeededMaxActionFor(AgentTriggerKinds.PeriodAutoClose).ShouldBe(ProactiveMaxAction.Hint);
        ProactiveGovernanceDefaults.SeededMaxActionOverrides.ContainsKey(AgentTriggerKinds.PeriodAutoClose).ShouldBeFalse();
    }
}
