// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests for TurnEvalSkillCatalog: case-insensitive lookup, first-wins on duplicate names and the detection of a
/// required parameter whose name ends in Id.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Evaluation.TurnEval;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Services.Assistant.Evaluation.TurnEval;

[TestFixture]
public class TurnEvalSkillCatalogTests
{
    private const string SkillName = "update_contract";

    private static AgentSkill Skill(string parametersJson, SkillEffect effect = SkillEffect.Mutate, string name = SkillName) => new()
    {
        Name = name,
        Effect = effect,
        ParametersJson = parametersJson
    };

    [Test]
    public void Lookup_IsCaseInsensitiveAndReturnsTheEffect()
    {
        var catalog = new TurnEvalSkillCatalog([Skill("[]", SkillEffect.Advise)]);

        catalog.TryGet(SkillName.ToUpperInvariant(), out var info).ShouldBeTrue();
        info.Effect.ShouldBe(SkillEffect.Advise);
    }

    [Test]
    public void Lookup_OfAnUnknownOrNullName_ReturnsFalse()
    {
        var catalog = new TurnEvalSkillCatalog([Skill("[]")]);

        catalog.TryGet("other", out _).ShouldBeFalse();
        catalog.TryGet(null, out _).ShouldBeFalse();
    }

    [Test]
    public void DuplicateNames_FirstSkillWins()
    {
        var catalog = new TurnEvalSkillCatalog([Skill("[]", SkillEffect.Read), Skill("[]", SkillEffect.Mutate)]);

        catalog.TryGet(SkillName, out var info).ShouldBeTrue();
        info.Effect.ShouldBe(SkillEffect.Read);
    }

    [TestCase("""[{"name":"clientId","required":true}]""", true)]
    [TestCase("""[{"Name":"ClientId","Required":true}]""", true)]
    [TestCase("""[{"name":"clientId","required":false}]""", false)]
    [TestCase("""[{"name":"clientName","required":true}]""", false)]
    [TestCase("""[{"name":"paid","required":true}]""", false)]
    [TestCase("[]", false)]
    [TestCase("{}", false)]
    [TestCase("not json", false)]
    public void RequiredIdParameter_IsDetectedFromTheParametersJson(string parametersJson, bool expected)
    {
        var catalog = new TurnEvalSkillCatalog([Skill(parametersJson)]);

        catalog.TryGet(SkillName, out var info).ShouldBeTrue();
        info.HasRequiredIdParameter.ShouldBe(expected);
    }
}
