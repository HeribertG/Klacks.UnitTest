// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for the rule that decides whether a learning cluster is evidence of a recipe trigger that matches
/// too broadly, read off the cluster's stored signal histogram.
/// </summary>

using Klacks.Api.Domain.Services.Assistant;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class RecipeDeclineClusterPolicyTests
{
    [Test]
    public void AHistogramDominatedByDeclines_IsATooBroadTrigger()
    {
        RecipeDeclineClusterPolicy
            .IsTriggerTooBroad("{\"recipe_declined\":4,\"refusal\":1}")
            .ShouldBeTrue();
    }

    [Test]
    public void AHistogramWithMoreRefusalsThanDeclines_IsAnOrdinaryCluster()
    {
        RecipeDeclineClusterPolicy
            .IsTriggerTooBroad("{\"recipe_declined\":1,\"refusal\":3}")
            .ShouldBeFalse();
    }

    // Where somebody stated something about the routing - named the wrong skill, said none was needed, or
    // marked the turn as not helpful - the cluster keeps the ordinary triage path however many declines
    // stand next to it. Dismissing it as a too-broad trigger would throw that statement away unread.
    [TestCase("{\"recipe_declined\":1,\"wrong_skill\":1}")]
    [TestCase("{\"recipe_declined\":9,\"wrong_skill\":1}")]
    [TestCase("{\"recipe_declined\":9,\"none_needed\":1}")]
    [TestCase("{\"recipe_declined\":9,\"explicit\":1}")]
    public void AHistogramCarryingAnExplicitHumanSignal_IsAnOrdinaryCluster(string signalKindsJson)
    {
        RecipeDeclineClusterPolicy.IsTriggerTooBroad(signalKindsJson).ShouldBeFalse();
    }

    // A counter at zero is not a statement, so it must not veto.
    [Test]
    public void AHistogramWhoseExplicitSignalIsZero_IsATooBroadTrigger()
    {
        RecipeDeclineClusterPolicy
            .IsTriggerTooBroad("{\"recipe_declined\":2,\"wrong_skill\":0}")
            .ShouldBeTrue();
    }

    // Refusals and inferred corrections carry no human verdict about the routing, so they keep ranking by
    // count - and a tie still counts as declines leading.
    [Test]
    public void AHistogramWithAsManyRefusalsAsDeclines_IsATooBroadTrigger()
    {
        RecipeDeclineClusterPolicy
            .IsTriggerTooBroad("{\"recipe_declined\":2,\"refusal\":2}")
            .ShouldBeTrue();
    }

    [Test]
    public void AHistogramWithAnInferredCorrection_IsATooBroadTrigger()
    {
        RecipeDeclineClusterPolicy
            .IsTriggerTooBroad("{\"recipe_declined\":4,\"implicit\":1}")
            .ShouldBeTrue();
    }

    [TestCase("{}")]
    [TestCase("{\"refusal\":3}")]
    [TestCase("not json at all")]
    [TestCase("")]
    [TestCase(null)]
    public void AHistogramWithoutDeclines_IsAnOrdinaryCluster(string? signalKindsJson)
    {
        RecipeDeclineClusterPolicy.IsTriggerTooBroad(signalKindsJson).ShouldBeFalse();
    }
}
