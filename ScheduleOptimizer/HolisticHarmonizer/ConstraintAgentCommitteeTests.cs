// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Committee;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Mutations;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.HolisticHarmonizer;

[TestFixture]
public class ConstraintAgentCommitteeTests
{
    private static readonly HarmonyBitmap StubBitmap = BuildStubBitmap();
    private static readonly PlanCellSwap StubSwap = new(0, 0, 1, 0, "stub");

    [Test]
    public void Evaluate_NoAgents_ReturnsApproved()
    {
        var committee = new ConstraintAgentCommittee([]);
        var decision = committee.Evaluate(StubBitmap, StubSwap);
        decision.Approved.ShouldBeTrue();
        decision.Verdicts.Count.ShouldBe(0);
        decision.Summary.ShouldBe(string.Empty);
    }

    [Test]
    public void Evaluate_AllAbstain_ReturnsApproved()
    {
        var committee = new ConstraintAgentCommittee(new IConstraintAgent[]
        {
            new FakeAgent("A", ConstraintAgentVote.Abstain),
            new FakeAgent("B", ConstraintAgentVote.Abstain),
            new FakeAgent("C", ConstraintAgentVote.Abstain),
        });
        var decision = committee.Evaluate(StubBitmap, StubSwap);
        decision.Approved.ShouldBeTrue();
    }

    [Test]
    public void Evaluate_SoloVetoWithoutCoalition_StillApproves()
    {
        // Single agent objecting alone is downgraded to a hint — needs at least
        // VetoCoalitionThreshold (=2) coordinated vetoes to actually block a swap.
        var committee = new ConstraintAgentCommittee(new IConstraintAgent[]
        {
            new FakeAgent("A", ConstraintAgentVote.Abstain),
            new FakeAgent("B", ConstraintAgentVote.Veto, "lone objection"),
            new FakeAgent("C", ConstraintAgentVote.Abstain),
            new FakeAgent("D", ConstraintAgentVote.Abstain),
            new FakeAgent("E", ConstraintAgentVote.Abstain),
        });
        var decision = committee.Evaluate(StubBitmap, StubSwap);
        decision.Approved.ShouldBeTrue();
    }

    [Test]
    public void Evaluate_TieVote_ReturnsApproved()
    {
        var committee = new ConstraintAgentCommittee(new IConstraintAgent[]
        {
            new FakeAgent("A", ConstraintAgentVote.Approve),
            new FakeAgent("B", ConstraintAgentVote.Veto),
        });
        var decision = committee.Evaluate(StubBitmap, StubSwap);
        decision.Approved.ShouldBeTrue();
    }

    [Test]
    public void Evaluate_StrictVetoMajority_ReturnsBlocked()
    {
        var committee = new ConstraintAgentCommittee(new IConstraintAgent[]
        {
            new FakeAgent("A", ConstraintAgentVote.Approve),
            new FakeAgent("B", ConstraintAgentVote.Veto, "first reason"),
            new FakeAgent("C", ConstraintAgentVote.Veto, "second reason"),
        });
        var decision = committee.Evaluate(StubBitmap, StubSwap);
        decision.Approved.ShouldBeFalse();
        decision.Summary.ShouldContain("B: first reason");
        decision.Summary.ShouldContain("C: second reason");
    }

    [Test]
    public void Evaluate_PreservesVerdictOrder()
    {
        var committee = new ConstraintAgentCommittee(new IConstraintAgent[]
        {
            new FakeAgent("Hours", ConstraintAgentVote.Approve),
            new FakeAgent("Pause", ConstraintAgentVote.Abstain),
            new FakeAgent("Consecutive", ConstraintAgentVote.Veto, "x"),
        });
        var decision = committee.Evaluate(StubBitmap, StubSwap);
        decision.Verdicts.Select(v => v.AgentName).ShouldBe(new[] { "Hours", "Pause", "Consecutive" });
    }

    private static HarmonyBitmap BuildStubBitmap()
    {
        var agents = new List<BitmapAgent>
        {
            new("a-0", "A0", 100m, new HashSet<CellSymbol>()),
            new("a-1", "A1", 100m, new HashSet<CellSymbol>()),
        };
        var input = new BitmapInput(agents, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 1), []);
        return BitmapBuilder.Build(input);
    }

    private sealed class FakeAgent : IConstraintAgent
    {
        private readonly ConstraintAgentVote _vote;
        private readonly string? _reason;

        public FakeAgent(string name, ConstraintAgentVote vote, string? reason = null)
        {
            Name = name;
            _vote = vote;
            _reason = reason;
        }

        public string Name { get; }

        public ConstraintAgentVerdict Evaluate(HarmonyBitmap before, PlanCellSwap swap)
            => new(Name, _vote, _reason);
    }
}
