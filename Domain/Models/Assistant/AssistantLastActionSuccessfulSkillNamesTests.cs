// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// AssistantLastAction.SuccessfulSkillNames is the one place the live chat and the headless replay read the previous
/// turn's skills from: successful calls only, in call order with each name at its last occurrence, and a
/// confirm_pending_action call named by the skill it replayed only when asked to (never on a correction turn).
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Assistant;

namespace Klacks.UnitTest.Domain.Models.Assistant;

[TestFixture]
public class AssistantLastActionSuccessfulSkillNamesTests
{
    private const string ReplayedSkill = "update_contract";
    private const string ReadSkill = "list_contracts";
    private const string OtherWriteSkill = "add_client_to_group";

    private static AssistantLastActionCall Call(string skillName, bool success = true, string? replayed = null) => new()
    {
        SkillName = skillName,
        Success = success,
        ReplayedSkillName = replayed
    };

    [Test]
    public void AConfirmedHeldAction_IsNamedByTheSkillItReplayed_WhenResolving()
    {
        var lastAction = new AssistantLastAction
        {
            Calls = [Call(AutonomyDefaults.ConfirmPendingActionSkillName, replayed: ReplayedSkill)]
        };

        lastAction.SuccessfulSkillNames(resolveReplayedSkills: true).ShouldBe([ReplayedSkill]);
    }

    [Test]
    public void AConfirmedHeldAction_KeepsItsOwnName_WhenNotResolving()
    {
        var lastAction = new AssistantLastAction
        {
            Calls = [Call(AutonomyDefaults.ConfirmPendingActionSkillName, replayed: ReplayedSkill)]
        };

        lastAction.SuccessfulSkillNames(resolveReplayedSkills: false)
            .ShouldBe([AutonomyDefaults.ConfirmPendingActionSkillName]);
    }

    [Test]
    public void FailedCalls_AreLeftOut_EvenWhenTheyCarryAReplayedName()
    {
        var lastAction = new AssistantLastAction
        {
            Calls =
            [
                Call(ReadSkill),
                Call(AutonomyDefaults.ConfirmPendingActionSkillName, success: false, replayed: ReplayedSkill)
            ]
        };

        lastAction.SuccessfulSkillNames(resolveReplayedSkills: true).ShouldBe([ReadSkill]);
    }

    [Test]
    public void TheOrder_IsCallOrder_WithEveryNameAtItsLastOccurrence()
    {
        var lastAction = new AssistantLastAction
        {
            Calls = [Call(OtherWriteSkill), Call(ReadSkill), Call(OtherWriteSkill.ToUpperInvariant())]
        };

        lastAction.SuccessfulSkillNames(resolveReplayedSkills: true)
            .ShouldBe([ReadSkill, OtherWriteSkill.ToUpperInvariant()]);
    }

    [Test]
    public void ContinuationSkillNames_OfTheImmediatelyPrecedingTurn_NameTheReplayedSkill()
    {
        var lastAction = new AssistantLastAction
        {
            Calls = [Call(AutonomyDefaults.ConfirmPendingActionSkillName, replayed: ReplayedSkill)]
        };

        lastAction.ContinuationSkillNames().ShouldBe([ReplayedSkill]);
    }

    [Test]
    public void ContinuationSkillNames_OfASupersededRecord_AreNull()
    {
        var lastAction = new AssistantLastAction
        {
            Calls = [Call(OtherWriteSkill)],
            SupersededAtUtc = DateTime.UtcNow
        };

        lastAction.ContinuationSkillNames().ShouldBeNull();
        lastAction.SuccessfulSkillNames(resolveReplayedSkills: false).ShouldBe([OtherWriteSkill]);
    }
}
