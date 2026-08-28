// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for the cluster lifecycle rules. The state machine is shared by the collector, the maintenance
/// sweep and the admin REST layer, so an illegal transition slipping through here would let one of them
/// silently resurrect a wish an administrator dismissed.
/// </summary>
namespace Klacks.UnitTest.Domain.Constants;

using Klacks.Api.Domain.Constants;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class SkillLearningStateMachineTests
{
    [Test]
    public void EveryStatus_HasATransitionList()
    {
        var statuses = new[]
        {
            SkillLearningClusterStatuses.Collecting,
            SkillLearningClusterStatuses.Ready,
            SkillLearningClusterStatuses.Learning,
            SkillLearningClusterStatuses.LearnedPhrase,
            SkillLearningClusterStatuses.LearnedCapability,
            SkillLearningClusterStatuses.Unfulfillable,
            SkillLearningClusterStatuses.Dismissed,
            SkillLearningClusterStatuses.Retired
        };

        foreach (var status in statuses)
        {
            SkillLearningStateMachine.AllowedTransitions.ShouldContainKey(status);
        }
    }

    [Test]
    public void TerminalStatuses_HaveNoWayOut()
    {
        foreach (var status in SkillLearningStateMachine.TerminalStatuses)
        {
            SkillLearningStateMachine.AllowedTransitions[status].ShouldBeEmpty();
            SkillLearningStateMachine.IsTerminal(status).ShouldBeTrue();
        }
    }

    [Test]
    public void TerminalStatuses_DoNotCount()
    {
        foreach (var status in SkillLearningStateMachine.TerminalStatuses)
        {
            SkillLearningStateMachine.IsCounting(status).ShouldBeFalse();
        }
    }

    [Test]
    public void CollectingToReady_IsLegal()
    {
        SkillLearningStateMachine
            .IsLegalTransition(SkillLearningClusterStatuses.Collecting, SkillLearningClusterStatuses.Ready)
            .ShouldBeTrue();
    }

    [Test]
    public void AFailedLearningRound_MayReturnToReady()
    {
        SkillLearningStateMachine
            .IsLegalTransition(SkillLearningClusterStatuses.Learning, SkillLearningClusterStatuses.Ready)
            .ShouldBeTrue();
    }

    [Test]
    public void CollectingCannotJumpStraightToLearned()
    {
        SkillLearningStateMachine
            .IsLegalTransition(SkillLearningClusterStatuses.Collecting, SkillLearningClusterStatuses.LearnedPhrase)
            .ShouldBeFalse();
    }

    [Test]
    public void ADismissedClusterCannotBeRevived()
    {
        SkillLearningStateMachine
            .IsLegalTransition(SkillLearningClusterStatuses.Dismissed, SkillLearningClusterStatuses.Collecting)
            .ShouldBeFalse();
    }

    [Test]
    public void EveryNonTerminalStatus_CanBeDismissed()
    {
        foreach (var status in SkillLearningStateMachine.AllowedTransitions.Keys)
        {
            if (SkillLearningStateMachine.IsTerminal(status))
            {
                continue;
            }

            SkillLearningStateMachine
                .IsLegalTransition(status, SkillLearningClusterStatuses.Dismissed)
                .ShouldBeTrue($"an administrator must be able to discard a cluster in status '{status}'");
        }
    }
}
