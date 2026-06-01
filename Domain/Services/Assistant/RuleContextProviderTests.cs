// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for RuleContextProvider (WP-P0.2, Variant A): the scheduling rule-pack is emitted only
/// when a curated scheduling skill is in scope, and it is a procedural nudge (read-skills + guardrail
/// + locked/break caution) carrying NO fixed limit numbers (so it reinforces, not contradicts, the
/// always-on "never assume fixed numbers" ontology rule).
/// </summary>

using Klacks.Api.Domain.Models.Scheduling;
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

    [Test]
    public void IsSchedulingContext_MatchesCuratedSetCaseInsensitive()
    {
        _sut.IsSchedulingContext(new[] { "Cover_Absence" }).ShouldBeTrue();
        _sut.IsSchedulingContext(new[] { "get_user_context" }).ShouldBeFalse();
        _sut.IsSchedulingContext(null).ShouldBeFalse();
    }

    [Test]
    public void ScopedClientPolicy_AppendsTightlyScopedBlock_AdditiveToNudge()
    {
        var policy = new SchedulingPolicy(
            MinRestHours: TimeSpan.FromHours(11),
            MaxDailyHours: TimeSpan.FromHours(9),
            MaxConsecutiveDays: 5,
            MaxWeeklyHours: TimeSpan.FromHours(42),
            MinRestDays: 2);

        var result = _sut.BuildSchedulingRulePack(new[] { "find_replacement" }, policy);

        // Additive: the nudge MUST still be present.
        result.ShouldContain("[SCHEDULING CONTEXT]");
        result.ShouldContain("get_scheduling_defaults");
        // Per-client block with the concrete resolved numbers.
        result.ShouldContain("[SELECTED-CLIENT EFFECTIVE LIMITS]");
        result.ShouldContain("9h");
        result.ShouldContain("42h");
        result.ShouldContain("5");
        // Tight scoping so the model cannot read these as global.
        result.ShouldContain("ONLY to the client currently open");
        result.ShouldContain("do not transfer");
    }

    [Test]
    public void NonSchedulingContext_WithPolicy_StillEmpty()
    {
        var policy = new SchedulingPolicy(
            TimeSpan.FromHours(11), TimeSpan.FromHours(10), 6, TimeSpan.FromHours(50), 2);

        _sut.BuildSchedulingRulePack(new[] { "get_user_context" }, policy).ShouldBeEmpty();
    }

    [Test]
    public void SchedulingContext_NoPolicy_OnlyNudge_NoClientBlock()
    {
        var result = _sut.BuildSchedulingRulePack(new[] { "cover_absence" }, scopedClientPolicy: null);

        result.ShouldContain("[SCHEDULING CONTEXT]");
        result.ShouldNotContain("[SELECTED-CLIENT EFFECTIVE LIMITS]");
    }
}
