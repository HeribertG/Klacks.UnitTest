// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Drift-guard tests for the hand-authored SkillEntityMap: every mapped entity must be a known
/// ontology entity, every map key must be a real [SkillImplementation] skill, and no key may be
/// blank. These fail loudly when skills are renamed/removed or an entity name drifts.
/// </summary>

using System.Reflection;
using Klacks.Api.Application.Services.Assistant.Ontology;
using Klacks.Api.Application.Services.Assistant.SkillGraph;
using Klacks.Api.Domain.Attributes;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Services.Assistant.SkillGraph;

[TestFixture]
public class SkillEntityMapTests
{
    [Test]
    public void EveryMappedEntity_IsAKnownOntologyEntity()
    {
        var known = new KlacksOntologyService().GetEntities().ToHashSet();

        foreach (var (skill, entities) in SkillEntityMap.Map)
        {
            entities.ShouldNotBeEmpty($"skill '{skill}' is mapped to an empty entity list");
            foreach (var entity in entities)
            {
                known.ShouldContain(entity, $"skill '{skill}' maps to unknown entity '{entity}'");
            }
        }
    }

    [Test]
    public void EveryMappedSkillName_IsARealSkillImplementation()
    {
        var realSkillNames = typeof(SkillEntityMap).Assembly
            .GetTypes()
            .Select(t => t.GetCustomAttribute<SkillImplementationAttribute>())
            .Where(attribute => attribute is not null)
            .Select(attribute => attribute!.SkillName)
            .ToHashSet();

        foreach (var skill in SkillEntityMap.Map.Keys)
        {
            realSkillNames.ShouldContain(skill, $"map key '{skill}' is not a real [SkillImplementation] skill");
        }
    }

    [Test]
    public void Map_HasNoBlankKeys()
    {
        foreach (var key in SkillEntityMap.Map.Keys)
        {
            key.ShouldNotBeNullOrWhiteSpace();
        }
    }
}
