// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for DraftLoopToolsetPolicy: an open planning-profile setup guarantees its loop skills and excludes
/// nothing; an open order review guarantees its loop skills and excludes the skills that would bypass its confirmed
/// preview; drafts of another conversation or user count for nothing; a failing store degrades to no rule.
/// </summary>

using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.Domain.Models.Assistant.OrderReview;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Application.Services.Assistant;

[TestFixture]
public class DraftLoopToolsetPolicyTests
{
    private const string Conversation = "conv-1";

    [Test]
    public void PlanningProfileDraft_GuaranteesItsLoop_AndExcludesNothing()
    {
        var store = PendingStoreTestFactory.CreatePlanningProfileDraftStore();
        var userId = Guid.NewGuid();
        store.Set(userId, Conversation, new PlanningProfileDraft());

        var toolset = PendingStoreTestFactory.CreateDraftLoopToolsetPolicy(planningProfileDraftStore: store)
            .Resolve(userId.ToString(), Conversation);

        Assert.That(toolset.GuaranteedSkillNames, Is.EquivalentTo(DraftLoopToolsetPolicy.PlanningProfileLoopSkillNames));
        Assert.That(toolset.ExcludedSkillNames, Is.Empty);
    }

    [Test]
    public void OrderReviewDraft_GuaranteesItsLoop_AndExcludesTheBypassSkills()
    {
        var store = PendingStoreTestFactory.CreateOrderReviewDraftStore();
        var userId = Guid.NewGuid();
        store.Set(userId, Conversation, new OrderReviewDraft());

        var toolset = PendingStoreTestFactory.CreateDraftLoopToolsetPolicy(orderReviewDraftStore: store)
            .Resolve(userId.ToString(), Conversation);

        Assert.That(toolset.GuaranteedSkillNames, Is.EquivalentTo(DraftLoopToolsetPolicy.OrderReviewLoopSkillNames));
        Assert.That(toolset.ExcludedSkillNames, Is.EquivalentTo(new[]
        {
            "update_shift", "seal_shift", "seal_open_orders", "cut_shift", "cut_shift_by_date",
            "assign_orders_to_groups", "add_shift_to_group",
        }));
    }

    [Test]
    public void ADraftOfAnotherConversation_OrAnUnreadableUser_CountsForNothing()
    {
        var store = PendingStoreTestFactory.CreateOrderReviewDraftStore();
        var userId = Guid.NewGuid();
        store.Set(userId, "other-conversation", new OrderReviewDraft());
        var policy = PendingStoreTestFactory.CreateDraftLoopToolsetPolicy(orderReviewDraftStore: store);

        Assert.That(policy.Resolve(userId.ToString(), Conversation).GuaranteedSkillNames, Is.Empty);
        Assert.That(policy.Resolve("not-a-guid", Conversation).GuaranteedSkillNames, Is.Empty);
    }

    [Test]
    public void AFailingStore_DegradesToNoRule()
    {
        var failing = Substitute.For<IPendingOrderReviewDraftStore>();
        failing.HasDraft(Arg.Any<Guid>(), Arg.Any<string>()).Returns(_ => throw new InvalidOperationException("db down"));
        var policy = new DraftLoopToolsetPolicy(
            PendingStoreTestFactory.CreatePlanningProfileDraftStore(), failing, NullLogger<DraftLoopToolsetPolicy>.Instance);

        var toolset = policy.Resolve(Guid.NewGuid().ToString(), Conversation);

        Assert.That(toolset.GuaranteedSkillNames, Is.Empty);
        Assert.That(toolset.ExcludedSkillNames, Is.Empty);
    }
}
