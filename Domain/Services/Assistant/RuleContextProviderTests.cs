// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for RuleContextProvider (WP-P0.2, Variant A): the scheduling rule-pack is emitted only
/// when a curated scheduling skill is in scope, and it is a procedural nudge (read-skills + guardrail
/// + locked/break caution) carrying NO fixed limit numbers (so it reinforces, not contradicts, the
/// always-on "never assume fixed numbers" ontology rule).
/// </summary>

using Klacks.Api.Domain.Services.Assistant;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class RuleContextProviderTests
{
    private readonly RuleContextProvider _sut = new();

    [Test]
    public void EmptyOrNull_ReturnsEmpty()
    {
        _sut.BuildSchedulingRulePack(null).ShouldBeEmpty();
        _sut.BuildSchedulingRulePack(new List<string>()).ShouldBeEmpty();
    }

    [Test]
    public void NonSchedulingSkillsOnly_ReturnsEmpty()
    {
        var result = _sut.BuildSchedulingRulePack(new[] { "get_user_context", "search_employees", "create_user" });

        result.ShouldBeEmpty();
    }

    [TestCase("find_replacement")]
    [TestCase("propose_plan")]
    [TestCase("cover_absence")]
    [TestCase("place_work")]
    [TestCase("read_schedule_state")]
    [TestCase("detect_conflicts")]
    public void SchedulingSkillInScope_ReturnsNudge(string schedulingSkill)
    {
        var result = _sut.BuildSchedulingRulePack(new[] { "get_user_context", schedulingSkill });

        result.ShouldContain("[SCHEDULING CONTEXT]");
        result.ShouldContain("get_scheduling_defaults");
        result.ShouldContain("list_scheduling_rules");
    }

    [Test]
    public void Nudge_IsCaseInsensitiveOnSkillNames()
    {
        var result = _sut.BuildSchedulingRulePack(new[] { "COVER_ABSENCE" });

        result.ShouldContain("[SCHEDULING CONTEXT]");
    }

    [Test]
    public void Nudge_CarriesNoFixedLimitNumbers()
    {
        var result = _sut.BuildSchedulingRulePack(new[] { "propose_plan" });

        // Must reinforce "never assume fixed numbers" — no hardcoded default values leak in.
        result.ShouldNotContain("11");
        result.ShouldNotContain("50");
        result.ShouldContain("never assume fixed numbers");
    }
}
