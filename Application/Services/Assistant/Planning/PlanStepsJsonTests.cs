// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for PlanStepsJson: the shared rule that decides what counts as a plan step, covering empty,
/// "[]", malformed, non-array, element-shape and valid StepsJson inputs.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Planning;

namespace Klacks.UnitTest.Application.Services.Assistant.Planning;

[TestFixture]
public class PlanStepsJsonTests
{
    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase("[]")]
    [TestCase("not json")]
    [TestCase("[{\"Skill\":")]
    [TestCase("{\"Skill\":\"create_shift\"}")]
    [TestCase("\"create_shift\"")]
    [TestCase("[{}]")]
    [TestCase("[{\"Order\":1}]")]
    [TestCase("[{\"Skill\":\"\"}]")]
    [TestCase("[{\"Skill\":\"   \"}]")]
    [TestCase("[{\"Skill\":5}]")]
    [TestCase("[{\"Skill\":null}]")]
    [TestCase("[1,\"create_shift\",null,[]]")]
    public void Parse_ReturnsNoSteps_ForInputWithoutValidStep(string? stepsJson)
    {
        PlanStepsJson.Parse(stepsJson).ShouldBeEmpty();
        PlanStepsJson.CountSteps(stepsJson).ShouldBe(0);
    }

    [Test]
    public void Parse_ReadsPascalCaseSkillAndVerifySkill()
    {
        var steps = PlanStepsJson.Parse("[{\"Order\":1,\"Skill\":\"create_shift\",\"VerifySkill\":\"get_shift\"}]");

        steps.Count.ShouldBe(1);
        steps[0].Skill.ShouldBe("create_shift");
        steps[0].VerifySkill.ShouldBe("get_shift");
    }

    [Test]
    public void Parse_ReadsCamelCaseSkillAndVerifySkill()
    {
        var steps = PlanStepsJson.Parse("[{\"skill\":\"create_shift\",\"verifySkill\":\"get_shift\"}]");

        steps.Count.ShouldBe(1);
        steps[0].Skill.ShouldBe("create_shift");
        steps[0].VerifySkill.ShouldBe("get_shift");
    }

    [Test]
    public void Parse_ReturnsNullVerifySkill_WhenAbsentOrNotAString()
    {
        var steps = PlanStepsJson.Parse("[{\"Skill\":\"a\"},{\"Skill\":\"b\",\"VerifySkill\":7}]");

        steps.Count.ShouldBe(2);
        steps.ShouldAllBe(s => s.VerifySkill == null);
    }

    [Test]
    public void Parse_SkipsInvalidElements_AndKeepsValidOnesInOrder()
    {
        var steps = PlanStepsJson.Parse("[{\"Skill\":\"first\"},{\"Order\":2},1,{\"Skill\":\"third\",\"VerifySkill\":\"check\"}]");

        steps.Select(s => s.Skill).ToList().ShouldBe(new[] { "first", "third" });
        PlanStepsJson.CountSteps("[{\"Skill\":\"first\"},{\"Order\":2},1,{\"Skill\":\"third\"}]").ShouldBe(2);
    }
}
