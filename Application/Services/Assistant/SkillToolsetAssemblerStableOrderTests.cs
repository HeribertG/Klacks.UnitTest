// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for SkillToolsetAssembler.ToStableOrderedFunctions: the tool array is part of the
/// prompt prefix every provider caches, so the same skill SET must always produce the same ordered
/// function list regardless of the (turn-dependent) retrieval and guarantee-insertion order.
/// </summary>

using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.Domain.Models.Assistant;

namespace Klacks.UnitTest.Application.Services.Assistant;

[TestFixture]
public class SkillToolsetAssemblerStableOrderTests
{
    private static AgentSkill MakeSkill(string name, bool alwaysOn = false) => new()
    {
        Name = name,
        AlwaysOn = alwaysOn,
        Description = name
    };

    [Test]
    public void SameSkillSet_InDifferentInputOrder_ProducesIdenticalFunctionOrder()
    {
        var skills = new[]
        {
            MakeSkill("update_client"),
            MakeSkill("navigate_to", alwaysOn: true),
            MakeSkill("create_employee"),
            MakeSkill("confirm_pending_action", alwaysOn: true)
        };
        var shuffled = new[] { skills[2], skills[0], skills[3], skills[1] };

        var namesA = SkillToolsetAssembler.ToStableOrderedFunctions(skills).Select(f => f.Name).ToList();
        var namesB = SkillToolsetAssembler.ToStableOrderedFunctions(shuffled).Select(f => f.Name).ToList();

        Assert.That(namesB, Is.EqualTo(namesA));
    }

    [Test]
    public void AlwaysOnSkills_ComeFirst_ThenOrdinalByName()
    {
        var skills = new[]
        {
            MakeSkill("update_client"),
            MakeSkill("navigate_to", alwaysOn: true),
            MakeSkill("create_employee"),
            MakeSkill("confirm_pending_action", alwaysOn: true)
        };

        var names = SkillToolsetAssembler.ToStableOrderedFunctions(skills).Select(f => f.Name).ToList();

        Assert.That(names, Is.EqualTo(new[]
        {
            "confirm_pending_action",
            "navigate_to",
            "create_employee",
            "update_client"
        }));
    }
}
