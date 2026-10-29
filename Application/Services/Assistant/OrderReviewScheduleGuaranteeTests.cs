// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for OrderReviewScheduleGuarantee: open_schedule is kept as a hint only when the immediately preceding turn
/// ran execute_order_review and the reply opens with an affirmation, in every core language.
/// </summary>

using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Assistant;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Services.Assistant;

[TestFixture]
public class OrderReviewScheduleGuaranteeTests
{
    private const string OpenSchedule = "open_schedule";
    private const string ExecuteOrderReview = "execute_order_review";

    private static AgentSkill Skill(string name) => new()
    {
        Name = name,
        Description = "A skill.",
        ParametersJson = "[]",
    };

    private static SkillToolsetGuaranteeRequest Request(string message, string language, IReadOnlyList<string>? continuation) =>
        new(
            Guid.NewGuid(), [Skill(OpenSchedule), Skill(ExecuteOrderReview)], [], message, null, null,
            Guid.NewGuid().ToString(), language, [], null, null, continuation, false);

    private static (HashSet<AgentSkill> Skills, Dictionary<string, ToolsetSkillSource> Sources) Apply(SkillToolsetGuaranteeRequest request)
    {
        var skills = new HashSet<AgentSkill>();
        var sources = new Dictionary<string, ToolsetSkillSource>();
        OrderReviewScheduleGuarantee.Apply(skills, sources, request);
        return (skills, sources);
    }

    [TestCase("Ja, zeig sie mir im Einsatzplan.", "de")]
    [TestCase("Yes, show them to me in the schedule.", "en")]
    [TestCase("Oui, montre-les-moi dans le planning.", "fr")]
    [TestCase("Sì, mostrameli nel piano di servizio.", "it")]
    public void AffirmativeReply_AfterExecuteOrderReview_KeepsOpenSchedule(string message, string language)
    {
        var (skills, sources) = Apply(Request(message, language, [ExecuteOrderReview]));

        skills.Select(s => s.Name).ShouldBe([OpenSchedule]);
        sources[OpenSchedule].ShouldBe(ToolsetSkillSource.Hint);
    }

    [Test]
    public void AffirmativeReply_WithoutExecuteOrderReviewBefore_KeepsNothing()
    {
        var (skills, _) = Apply(Request("Ja, zeig sie mir im Einsatzplan.", "de", ["preview_order_review"]));

        skills.ShouldBeEmpty();
    }

    [Test]
    public void NoContinuation_KeepsNothing()
    {
        var (skills, _) = Apply(Request("Ja, zeig sie mir im Einsatzplan.", "de", null));

        skills.ShouldBeEmpty();
    }

    [Test]
    public void NegativeReply_AfterExecuteOrderReview_KeepsNothing()
    {
        var (skills, _) = Apply(Request("Nein, danke.", "de", [ExecuteOrderReview]));

        skills.ShouldBeEmpty();
    }
}
