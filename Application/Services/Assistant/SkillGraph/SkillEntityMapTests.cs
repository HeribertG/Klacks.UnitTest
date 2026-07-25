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

    private static readonly string[] GroupingAndGeocodingSkills =
    [
        "propose_grouping", "apply_grouping",
        "add_client_to_nearest_group", "group_ungrouped_by_city_name",
        "geocode_location_groups", "set_group_location", "check_group_geocoding_status"
    ];

    [Test]
    public void GroupingAndGeocodingSkills_AreMapped()
    {
        foreach (var skill in GroupingAndGeocodingSkills)
        {
            SkillEntityMap.Map.Keys.ShouldContain(
                skill,
                $"'{skill}' is unmapped: the substrate prior derives no edge for it and " +
                "EntityChangeNotifier cannot resolve an entity from its name, so no page reload is pushed.");
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
